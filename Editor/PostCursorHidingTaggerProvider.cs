using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using Microsoft.VisualStudio.Utilities;

namespace OllamaCodeCompletions
{
    [Export(typeof(IViewTaggerProvider))]
    [ContentType("code")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    [TagType(typeof(IntraTextAdornmentTag))]
    internal sealed class PostCursorHidingTaggerProvider : IViewTaggerProvider
    {
        public ITagger<T> CreateTagger<T>(ITextView textView, ITextBuffer buffer) where T : ITag
        {
            if (!(textView is IWpfTextView wpfView)) return null;
            if (!ViewFilter.ShouldAttach(wpfView, "PostCursorHidingTaggerProvider")) return null;
            if (textView.TextBuffer != buffer) return null;

            SuggestionSession session = SuggestionSession.GetOrCreate(wpfView);
            return new PostCursorHidingTagger(wpfView, session) as ITagger<T>;
        }
    }
}
