using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Code-behind for the WPF Options page UI. The hosting <see cref="OptionsPage"/>
    /// sets <c>DataContext = this-page</c> on activation; XAML bindings cover all
    /// simple properties. Two things bindings can't cleanly do are handled here:
    /// the unbound <see cref="PasswordBox"/> (loaded on activation, saved on apply)
    /// and the runtime-only model list populated by the Refresh button.
    /// </summary>
    public partial class OptionsPageControl : UserControl
    {
        public OptionsPageControl()
        {
            InitializeComponent();
            LogPathTextBox.Text = Logger.LogFilePath;
        }

        /// <summary>
        /// Pulls fields from <see cref="OptionsPage"/> / <see cref="CredentialStorage"/>
        /// that bindings can't reach. Called by <see cref="OptionsPage.OnActivate"/>.
        /// </summary>
        internal void LoadFromPage()
        {
            if (DataContext is OptionsPage page)
            {
                PasswordBox.Password = CredentialStorage.GetPassword() ?? string.Empty;

                // Re-bind ItemsSource each activation — the list is mutated by the
                // Refresh handler and might have changed while the dialog was closed.
                ModelComboBox.ItemsSource = page.AvailableModels;
            }
        }

        /// <summary>
        /// Pushes fields back into <see cref="CredentialStorage"/> that bindings
        /// don't handle (the unbound <see cref="PasswordBox"/>).
        /// Called by <see cref="OptionsPage.OnApply"/>.
        /// </summary>
        internal void SaveToPage()
        {
            if (DataContext is OptionsPage page)
            {
                string username = page.Username ?? string.Empty;
                string password = PasswordBox.Password ?? string.Empty;
                CredentialStorage.Save(username, password);
            }
        }

        // -------- Refresh models --------

        private async void OnRefreshModelsClicked(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is OptionsPage page)) return;

            // Read live UI state directly so the user doesn't have to commit each
            // field via lost-focus before clicking the button.
            string serverUrl = ServerUrlTextBox.Text;
            bool useAuth = UseAuthCheckBox.IsChecked == true;
            string username = UsernameTextBox.Text;
            string password = PasswordBox.Password;

            RefreshButton.IsEnabled = false;
            StatusText.Text = "Refreshing…";
            try
            {
                var models = await OllamaClient.ListModelsAsync(
                    serverUrl, useAuth, username, password, CancellationToken.None);

                page.AvailableModels = models;
                ModelComboBox.ItemsSource = models;
                StatusText.Text = $"Found {models.Count} model(s).";
            }
            catch (Exception ex)
            {
                Logger.LogException("Options", ex);
                StatusText.Text = $"Refresh failed: {ex.Message}";
            }
            finally
            {
                RefreshButton.IsEnabled = true;
            }
        }

        // -------- Test connection --------

        private async void OnTestConnectionClicked(object sender, RoutedEventArgs e)
        {
            if (!(DataContext is OptionsPage page)) return;

            string serverUrl = ServerUrlTextBox.Text;
            string model = ModelComboBox.Text;
            bool useAuth = UseAuthCheckBox.IsChecked == true;
            string username = UsernameTextBox.Text;
            string password = PasswordBox.Password;

            TestButton.IsEnabled = false;
            StatusText.Text = "Testing…";
            var sw = Stopwatch.StartNew();
            try
            {
                var result = await OllamaClient.TestConnectionAsync(
                    serverUrl, model, useAuth, username, password, CancellationToken.None);
                sw.Stop();

                StatusText.Text = result.Success
                    ? $"OK ({sw.ElapsedMilliseconds}ms). {result.Message}"
                    : $"Failed: {result.Message}";
            }
            catch (Exception ex)
            {
                Logger.LogException("Options", ex);
                StatusText.Text = $"Error: {ex.Message}";
            }
            finally
            {
                TestButton.IsEnabled = true;
            }
        }

        // -------- Open log file --------

        private void OnOpenLogClicked(object sender, RoutedEventArgs e)
        {
            try
            {
                string path = Logger.LogFilePath;
                if (File.Exists(path))
                {
                    Process.Start(new ProcessStartInfo
                    {
                        FileName = path,
                        UseShellExecute = true,
                    });
                }
                else
                {
                    StatusText.Text = $"Log file not yet created: {path}";
                }
            }
            catch (Exception ex)
            {
                Logger.LogException("Options", ex);
                StatusText.Text = $"Open failed: {ex.Message}";
            }
        }
    }
}
