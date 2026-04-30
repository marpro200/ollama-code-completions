using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.VisualStudio.Shell;
using CancelEventArgs = System.ComponentModel.CancelEventArgs;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Tools → Options → Ollama Code Completions → General.
    ///
    /// Backed by a custom WPF UI (<see cref="OptionsPageControl"/>). All persisted
    /// properties keep their original names, defaults, and attributes — VS persists
    /// them by reflection regardless of how they're rendered, and the [Category] /
    /// [DisplayName] / [Description] metadata is still honored by Tools → Import
    /// and Export Settings.
    ///
    /// <see cref="Username"/> and <see cref="Password"/> still round-trip through
    /// <see cref="CredentialStorage"/>; the password specifically is loaded into
    /// the unbound <see cref="OptionsPageControl.PasswordBox"/> on activate and
    /// saved back on apply (see <see cref="OptionsPageControl"/>).
    /// </summary>
    [Guid("a7e2f8b3-9c14-4f2d-8e6b-1f5d3c9a8e72")]
    [ComVisible(true)]
    public class OptionsPage : UIElementDialogPage
    {
        private const string CategoryConnection = "Connection";
        private const string CategoryAuthentication = "Authentication";
        private const string CategoryBehavior = "Behavior";
        private const string PasswordSentinel = "********";

        // --- Connection ---

        [Category(CategoryConnection)]
        [DisplayName("Server URL")]
        [Description("Base URL of the Ollama server, e.g. https://ollama.yourdomain.com (no trailing /api/generate).")]
        public string ServerUrl { get; set; } = "http://localhost:11434";

        [Category(CategoryConnection)]
        [DisplayName("Model")]
        [Description("Model name to use for FIM completion, e.g. qwen2.5-coder:1.5b.")]
        public string Model { get; set; } = "qwen2.5-coder:1.5b";

        // --- Authentication ---

        [Category(CategoryAuthentication)]
        [DisplayName("Use HTTP Basic authentication")]
        [Description("If enabled, sends an Authorization: Basic <base64> header on every Ollama API request.")]
        public bool UseAuthentication { get; set; } = false;

        [Category(CategoryAuthentication)]
        [DisplayName("Username")]
        [Description("HTTP Basic auth username. Stored alongside the password in the Windows Credential Manager.")]
        public string Username
        {
            get => CredentialStorage.GetUsername() ?? string.Empty;
            set => CredentialStorage.Save(value ?? string.Empty, CredentialStorage.GetPassword() ?? string.Empty);
        }

        [Category(CategoryAuthentication)]
        [DisplayName("Password")]
        [Description("HTTP Basic auth password. Stored securely in the Windows Credential Manager (target: OllamaCopilot:Auth).")]
        [PasswordPropertyText(true)]
        public string Password
        {
            // Show a sentinel rather than the real password so it never leaves the credential store
            // (Import/Export Settings reads property values; we don't want the cleartext leaked).
            get => string.IsNullOrEmpty(CredentialStorage.GetPassword()) ? string.Empty : PasswordSentinel;
            set
            {
                if (value == PasswordSentinel) return;       // unchanged in the dialog
                CredentialStorage.Save(CredentialStorage.GetUsername() ?? string.Empty, value ?? string.Empty);
            }
        }

        // --- Behavior ---

        [Category(CategoryBehavior)]
        [DisplayName("Enabled")]
        [Description("Globally enable or disable inline suggestions.")]
        public bool Enabled { get; set; } = true;

        [Category(CategoryBehavior)]
        [DisplayName("Debounce delay (ms)")]
        [Description("Idle time after the last keystroke before requesting a suggestion. Default: 300 ms.")]
        public int DebounceMs { get; set; } = 300;

        [Category(CategoryBehavior)]
        [DisplayName("Max prefix characters")]
        [Description("Maximum number of characters before the cursor sent as context.")]
        public int MaxPrefixChars { get; set; } = 4096;

        [Category(CategoryBehavior)]
        [DisplayName("Max suffix characters")]
        [Description("Maximum number of characters after the cursor sent as context.")]
        public int MaxSuffixChars { get; set; } = 1024;

        [Category(CategoryBehavior)]
        [DisplayName("Max tokens to predict")]
        [Description("Upper bound on tokens to generate per suggestion (Ollama num_predict).")]
        public int MaxPredict { get; set; } = 128;

        [Category(CategoryBehavior)]
        [DisplayName("Request timeout (seconds)")]
        [Description("HTTP timeout for each Ollama request.")]
        public int TimeoutSeconds { get; set; } = 30;

        // --- Diagnostics ---
        // Backing fields hold the persisted state; setters mirror to Logger so it
        // never has to reach into Options on the hot path. OnActivate / OnApply
        // re-push as belt-and-braces in case persistence ever bypasses the setter.

        private bool _logToFile = false;
        [Category(CategoryBehavior)]
        [DisplayName("Log to file")]
        [Description("Write diagnostic events to %TEMP%\\OllamaCodeCompletions.log. Useful for bug reports.")]
        public bool LogToFile
        {
            get => _logToFile;
            set
            {
                _logToFile = value;
                Logger.FileEnabled = value;
            }
        }

        private bool _logToOutputPane = false;
        [Category(CategoryBehavior)]
        [DisplayName("Log to Output pane")]
        [Description("Show diagnostic events in a dedicated 'Ollama Code Completions' pane in the Output window. Useful for live debugging.")]
        public bool LogToOutputPane
        {
            get => _logToOutputPane;
            set
            {
                _logToOutputPane = value;
                Logger.OutputPaneEnabled = value;
            }
        }

        // --- Runtime-only state for the WPF UI ---
        // Populated by the "Refresh" button. Hidden from the property grid AND
        // from Import/Export Settings so a transient list of model names doesn't
        // get persisted to user settings XML.

        [Browsable(false)]
        [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
        public List<string> AvailableModels { get; set; } = new List<string>();

        // --- WPF host plumbing ---

        private OptionsPageControl _control;

        protected override UIElement Child
        {
            get
            {
                if (_control == null) _control = new OptionsPageControl();
                return _control;
            }
        }

        protected override void OnActivate(CancelEventArgs e)
        {
            base.OnActivate(e);
            Logger.FileEnabled = _logToFile;
            Logger.OutputPaneEnabled = _logToOutputPane;

            // Bind the WPF control's DataContext to this page so XAML bindings
            // resolve, then explicitly re-load fields that bindings can't reach
            // (the password). Re-loading every activation is necessary because
            // re-opening the dialog reuses the same control instance, so a
            // DataContextChanged handler wouldn't fire.
            var c = (OptionsPageControl)Child;
            c.DataContext = this;
            c.LoadFromPage();
        }

        protected override void OnApply(PageApplyEventArgs e)
        {
            Logger.FileEnabled = _logToFile;
            Logger.OutputPaneEnabled = _logToOutputPane;

            // Persist the unbound PasswordBox via CredentialStorage. Username
            // already flows through normal data binding + its own setter.
            ((OptionsPageControl)Child).SaveToPage();

            base.OnApply(e);
        }
    }
}
