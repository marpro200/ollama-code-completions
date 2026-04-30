using System;
using System.IO;
#if !TEST
using EnvDTE;
using EnvDTE80;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
#endif

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Builds the single-line file-path comment that is prepended to the Ollama prompt prefix.
    /// The VS-specific entry point lives inside a #if !TEST guard so the pure helper functions
    /// can be compiled and tested without any VS assembly references.
    /// </summary>
    internal static class FileHeaderBuilder
    {
#if !TEST
        /// <summary>
        /// Returns a comment line such as "// File: src/Services/UserService.cs\n",
        /// or null when the view has no real backing file (scratch buffers, output panes, etc.).
        /// Must be called on the UI thread.
        /// </summary>
        internal static string TryBuildFileHeader(IWpfTextView view)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (!view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument doc))
                    return null;

                string fullPath = doc.FilePath;
                if (string.IsNullOrEmpty(fullPath))
                    return null;

                string solutionRoot = TryGetSolutionRoot();
                string displayPath  = GetDisplayPath(fullPath, solutionRoot);
                string ext          = Path.GetExtension(fullPath).TrimStart('.').ToLowerInvariant();
                return FormatHeader(displayPath, GetCommentStart(ext));
            }
            catch
            {
                return null;
            }
        }

        private static string TryGetSolutionRoot()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (Package.GetGlobalService(typeof(EnvDTE.DTE)) is DTE2 dte)
                {
                    string slnFull = dte.Solution?.FullName;
                    if (!string.IsNullOrEmpty(slnFull))
                        return Path.GetDirectoryName(slnFull);
                }
            }
            catch { }
            return null;
        }
#endif

        // ── pure helpers — no VS dependencies, always compiled ────────────────────

        /// <summary>
        /// Returns the path to show in the header: solution-relative with forward slashes
        /// when the file is inside the solution tree, bare filename otherwise.
        /// Returns null when <paramref name="fullPath"/> is null or empty.
        /// Result is capped at 200 characters (last 200 kept).
        /// </summary>
        internal static string GetDisplayPath(string fullPath, string solutionRoot)
        {
            if (string.IsNullOrEmpty(fullPath)) return null;

            string result;
            if (!string.IsNullOrEmpty(solutionRoot))
            {
                // Normalise: ensure the root ends with exactly one separator so
                // "C:\Repo" doesn't accidentally match "C:\Repository\…".
                string root = solutionRoot.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
                result = fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                    ? fullPath.Substring(root.Length).Replace('\\', '/')
                    : Path.GetFileName(fullPath);
            }
            else
            {
                result = Path.GetFileName(fullPath);
            }

            // Paranoia against pathologically deep paths blowing up the prompt.
            return result.Length > 200 ? result.Substring(result.Length - 200) : result;
        }

        /// <summary>
        /// Returns the opening token for a line comment appropriate for the file type.
        /// For XML-style languages returns "<!--" (sentinel; FormatHeader closes it).
        /// Defaults to "//" for unrecognised extensions.
        /// </summary>
        internal static string GetCommentStart(string ext)
        {
            switch (ext)
            {
                case "py":  case "rb":  case "sh":   case "bash": case "zsh":
                case "ps1": case "yaml": case "yml": case "toml":
                case "dockerfile": case "makefile": case "r": case "pl":
                    return "#";

                case "sql": case "lua": case "hs":
                    return "--";

                case "vb":
                    return "'";

                case "html": case "xml":    case "xaml": case "svg":
                case "vue":  case "razor":  case "cshtml": case "vbhtml":
                    return "<!--";

                default:
                    return "//";
            }
        }

        /// <summary>
        /// Formats the complete header line with a trailing LF.
        /// Returns null when <paramref name="displayPath"/> is null or empty.
        /// </summary>
        internal static string FormatHeader(string displayPath, string commentStart)
        {
            if (string.IsNullOrEmpty(displayPath)) return null;

            // XML-style comment wraps the whole thing: <!-- File: path -->
            if (commentStart == "<!--")
                return $"<!-- File: {displayPath} -->\n";

            return $"{commentStart} File: {displayPath}\n";
        }
    }
}
