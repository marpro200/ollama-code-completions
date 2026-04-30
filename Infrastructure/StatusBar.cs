using System.Threading.Tasks;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Tiny helper for poking the VS status bar from background threads.
    /// All operations are best-effort and silently swallow failures — the
    /// status bar going dark must never break suggestion flow.
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
            catch
            {
                // Best-effort.
            }
        }

        public static Task ClearAsync() => SetAsync(string.Empty);
    }
}
