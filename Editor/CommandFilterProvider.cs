using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Editor;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.TextManager.Interop;
using Microsoft.VisualStudio.Utilities;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Hooks <see cref="CommandFilter"/> into the IVsTextView command-target chain.
    /// We have to use IVsTextView (not IWpfTextView) because IOleCommandTarget routing
    /// is the legacy path — it's the only place we can intercept Tab/Esc before the
    /// editor's own handlers see them.
    /// </summary>
    [Export(typeof(IVsTextViewCreationListener))]
    [ContentType("code")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    internal sealed class CommandFilterProvider : IVsTextViewCreationListener
    {
#pragma warning disable CS0649 // assigned by MEF
        [Import] internal IVsEditorAdaptersFactoryService AdaptersFactory;
#pragma warning restore CS0649

        public void VsTextViewCreated(IVsTextView textViewAdapter)
        {
            var view = AdaptersFactory?.GetWpfTextView(textViewAdapter);
            if (!ViewFilter.ShouldAttach(view, "CommandFilterProvider")) return;

            var filter = new CommandFilter(view);
            textViewAdapter.AddCommandFilter(filter, out var next);
            filter.Next = next;
        }
    }
}
