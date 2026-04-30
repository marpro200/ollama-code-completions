using System;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Task = System.Threading.Tasks.Task;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Top-level VS package. Its main jobs are:
    /// 1. Register the Tools → Options page so users can configure the extension.
    /// 2. Auto-load early so <see cref="Instance"/> is available to MEF components
    ///    (the suggestion session, command filter) by the time the user starts typing.
    /// </summary>
    [PackageRegistration(UseManagedResourcesOnly = true, AllowsBackgroundLoading = true)]
    [InstalledProductRegistration("Ollama Code Completions", "Inline ghost-text autocomplete via Ollama.", "1.0.2")]
    [Guid(PackageGuidString)]
    [ProvideOptionPage(typeof(OptionsPage), "Ollama Code Completions", "General", 0, 0, true)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.NoSolution_string, PackageAutoLoadFlags.BackgroundLoad)]
    [ProvideAutoLoad(VSConstants.UICONTEXT.SolutionExists_string, PackageAutoLoadFlags.BackgroundLoad)]
    public sealed class OllamaCodeCompletionsPackage : AsyncPackage
    {
        public const string PackageGuidString = "f3d1e8a4-2c5e-4c1a-9ff7-3b9e4c8a7d51";

        public static OllamaCodeCompletionsPackage Instance { get; private set; }

        protected override async Task InitializeAsync(CancellationToken cancellationToken, IProgress<ServiceProgressData> progress)
        {
            await base.InitializeAsync(cancellationToken, progress);
            await JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            Instance = this;
        }

        /// <summary>
        /// Returns the (cached) Tools → Options page. Must be called on the UI thread.
        /// </summary>
        public OptionsPage Options
        {
            get
            {
                ThreadHelper.ThrowIfNotOnUIThread();
                return (OptionsPage)GetDialogPage(typeof(OptionsPage));
            }
        }
    }
}
