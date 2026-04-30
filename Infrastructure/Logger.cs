using System;
using System.IO;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Diagnostic logger with two independently-toggleable sinks: a rolling file in
    /// %TEMP% and a dedicated "Ollama Code Completions" Output-window pane.
    ///
    /// All write paths swallow their own failures — the logger must NEVER throw out.
    /// (Letting it throw would either crash the IDE or, worse, create exception loops
    /// when callers catch and try to log the failure.)
    ///
    /// Categories used by the rest of the codebase:
    ///   Attach       — text view created (content type, roles, file path)
    ///   Request      — keystroke triggered a fresh request (caret + length only)
    ///   Cache        — HIT (exact) / HIT (extension) / MISS / STORE / CLEAR
    ///   Http         — POST start, response status + ms, errors
    ///   PostProcess  — input length, output length
    ///   Render       — final length, lines, multi-line gap
    ///   Command      — TAB accepted / ESC dismissed
    ///   Options      — model changed
    ///   Credential   — Credential Manager read result (Win32 error code)
    ///   FileHeader   — DTE / solution path lookup failures
    ///   StatusBar    — best-effort status bar write failures
    ///   Error        — caught exceptions from anywhere else
    ///
    /// What is INTENTIONALLY not logged: prefix/suffix/completion text contents,
    /// authentication credentials, full Ollama responses. Lengths and timings only.
    /// </summary>
    internal static class Logger
    {
        // Stable GUID for the Output-window pane. NEVER change — VS persists pane
        // ordering by GUID, and renaming would create a duplicate pane on existing
        // installs while orphaning the old one.
        private static readonly Guid PaneGuid =
            new Guid("b8d4f2a1-7c3e-4f8b-9d1c-3e5a7c9b2d4f");

        private const string PaneTitle = "Ollama Code Completions";
        private const long MaxFileBytes = 5L * 1024 * 1024;

        private static readonly object s_fileLock = new object();
        private static readonly object s_paneLock = new object();
        private static IVsOutputWindowPane s_pane;

        public static bool FileEnabled { get; set; }
        public static bool OutputPaneEnabled { get; set; }

        public static string LogFilePath { get; } =
            Path.Combine(Path.GetTempPath(), "OllamaCodeCompletions.log");

        public static void Log(string category, string message)
        {
            if (!FileEnabled && !OutputPaneEnabled) return;

            string line = $"{DateTime.Now:HH:mm:ss.fff} [{category}] {message}";

            if (FileEnabled) WriteFile(line);
            if (OutputPaneEnabled) WritePane(line);
        }

        public static void LogException(string category, Exception ex)
        {
            if (ex == null) return;
            Log(category, $"{ex.GetType().Name}: {ex.Message}");
        }

        // ---- file sink ----

        private static void WriteFile(string line)
        {
            try
            {
                lock (s_fileLock)
                {
                    string path = LogFilePath;
                    var info = new FileInfo(path);
                    if (info.Exists && info.Length > MaxFileBytes)
                    {
                        string archive = path + ".1";
                        try { if (File.Exists(archive)) File.Delete(archive); } catch { }
                        try { File.Move(path, archive); } catch { }
                    }
                    File.AppendAllText(path, line + Environment.NewLine);
                }
            }
            catch
            {
                // Logger must never throw out — see class comment.
            }
        }

        // ---- output-pane sink ----

        private static void WritePane(string line)
        {
            try
            {
                IVsOutputWindowPane pane = EnsurePane();
                pane?.OutputStringThreadSafe(line + Environment.NewLine);
            }
            catch
            {
                // Logger must never throw out — see class comment.
            }
        }

        private static IVsOutputWindowPane EnsurePane()
        {
            if (s_pane != null) return s_pane;

            try
            {
                // Pane creation requires the UI thread. Sync-block via JTF so callers
                // on background threads can still log via the pane sink.
                ThreadHelper.JoinableTaskFactory.Run(async () =>
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    lock (s_paneLock)
                    {
                        if (s_pane != null) return;
                        if (!(Package.GetGlobalService(typeof(SVsOutputWindow)) is IVsOutputWindow ow))
                            return;
                        var paneGuid = PaneGuid;
                        ow.CreatePane(ref paneGuid, PaneTitle, fInitVisible: 1, fClearWithSolution: 0);
                        ow.GetPane(ref paneGuid, out s_pane);
                    }
                });
            }
            catch
            {
                // Logger must never throw out — see class comment.
            }

            return s_pane;
        }
    }
}
