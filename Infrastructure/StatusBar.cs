using System;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Tiny helper for poking the VS status bar from background threads.
    /// All operations are best-effort — the status bar going dark must never
    /// break suggestion flow. Bare catch is justified here as a must-not-throw
    /// boundary; failures are still logged so they aren't silent.
    /// </summary>
    internal static class StatusBar
    {
        public static async Task SetAsync(string text)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (Package.GetGlobalService(typeof(SVsStatusbar)) is IVsStatusbar bar)
                {
                    bar.IsFrozen(out int frozen);
                    if (frozen == 0) bar.SetText(text);
                }
            }
            catch (Exception ex)
            {
                // Bare catch is justified: status bar updates are best-effort and must
                // never propagate. Log so failures are at least diagnosable.
                Logger.LogException("StatusBar", ex);
            }
        }

        public static Task ClearAsync() => SetAsync(string.Empty);
    }
}
