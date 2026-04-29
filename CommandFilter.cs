using System;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.OLE.Interop;
using Microsoft.VisualStudio.Text.Editor;

namespace OllamaCopilot
{
    /// <summary>
    /// Sits in the editor's IOleCommandTarget chain and steals Tab / Escape
    /// when (and only when) an Ollama suggestion is currently visible.
    ///
    /// Tab → accept the suggestion (insert into buffer, move caret).
    /// Esc → dismiss the suggestion.
    ///
    /// All other commands (including Tab when there's no suggestion) flow
    /// through unchanged so we don't break the editor's normal behavior.
    /// </summary>
    internal sealed class CommandFilter : IOleCommandTarget
    {
        public IOleCommandTarget Next { get; set; }
        private readonly IWpfTextView _view;

        public CommandFilter(IWpfTextView view)
        {
            _view = view;
        }

        public int QueryStatus(ref Guid pguidCmdGroup, uint cCmds, OLECMD[] prgCmds, IntPtr pCmdText)
        {
            return Next?.QueryStatus(ref pguidCmdGroup, cCmds, prgCmds, pCmdText)
                   ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }

        public int Exec(ref Guid pguidCmdGroup, uint nCmdID, uint nCmdexecopt, IntPtr pvaIn, IntPtr pvaOut)
        {
            try
            {
                var session = SuggestionSession.GetOrCreate(_view);

                if (pguidCmdGroup == VSConstants.VSStd2K && session.HasActiveSuggestion)
                {
                    var id = (VSConstants.VSStd2KCmdID)nCmdID;
                    if (id == VSConstants.VSStd2KCmdID.TAB || id == VSConstants.VSStd2KCmdID.BACKTAB)
                    {
                        if (session.AcceptSuggestion()) return VSConstants.S_OK;
                    }
                    else if (id == VSConstants.VSStd2KCmdID.CANCEL)
                    {
                        session.DismissSuggestion();
                        return VSConstants.S_OK;
                    }
                    else if (id == VSConstants.VSStd2KCmdID.RETURN)
                    {
                        // Pressing Enter while a ghost is showing should drop it
                        // and let the editor handle the newline normally.
                        session.DismissSuggestion();
                        // fall through
                    }
                }
            }
            catch
            {
                // Never let our filter break command routing.
            }

            return Next?.Exec(ref pguidCmdGroup, nCmdID, nCmdexecopt, pvaIn, pvaOut)
                   ?? (int)Constants.OLECMDERR_E_NOTSUPPORTED;
        }
    }
}
