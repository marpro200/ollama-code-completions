using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using Microsoft.VisualStudio.Utilities;

namespace OllamaCodeCompletions
{
    [Export(typeof(ILineTransformSourceProvider))]
    [ContentType("code")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    internal sealed class GhostTextLineTransformSourceProvider : ILineTransformSourceProvider
    {
        public ILineTransformSource Create(IWpfTextView textView)
        {
            if (!ViewFilter.ShouldAttach(textView, "GhostTextLineTransformSourceProvider")) return null;

            return new GhostTextLineTransformSource(SuggestionSession.GetOrCreate(textView));
        }
    }

    internal sealed class GhostTextLineTransformSource : ILineTransformSource
    {
        private readonly SuggestionSession _session;

        internal GhostTextLineTransformSource(SuggestionSession session)
        {
            _session = session;
        }

        public LineTransform GetLineTransform(ITextViewLine line, double yPosition, ViewRelativePosition placement)
        {
            int extraLines = _session.GetExtraLinesFor(line);
            if (extraLines <= 0)
                return line.DefaultLineTransform;

            // Reserve extraLines * TextHeight of bottom space below the anchor line's
            // text so subsequent real lines are pushed down and ghost lines 2..N can be
            // drawn into that gap without overlapping real code.
            return new LineTransform(
                line.DefaultLineTransform.TopSpace,
                line.DefaultLineTransform.BottomSpace + extraLines * line.TextHeight,
                line.DefaultLineTransform.VerticalScale);
        }
    }
}
