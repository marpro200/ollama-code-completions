using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Tagging;
using System;
using System.Collections.Generic;
using System.Windows.Controls;

namespace OllamaCodeCompletions
{
    internal sealed class PostCursorHidingTagger : ITagger<IntraTextAdornmentTag>, IDisposable
    {
        private readonly IWpfTextView _view;
        private readonly SuggestionSession _session;

        public event EventHandler<SnapshotSpanEventArgs> TagsChanged;

        public PostCursorHidingTagger(IWpfTextView view, SuggestionSession session)
        {
            _view = view;
            _session = session;
            _session.SuggestionStateChanged += OnSuggestionStateChanged;
        }

        public IEnumerable<ITagSpan<IntraTextAdornmentTag>> GetTags(NormalizedSnapshotSpanCollection spans)
        {
            if (!_session.HasActiveSuggestion || _session.CurrentInsertionMode != InsertionMode.ReplaceToEndOfLine)
                yield break;

            ITextSnapshot snapshot = spans.Count > 0 ? spans[0].Snapshot : _view.TextSnapshot;
            int cursorPos = _session.GetAnchorPosition(snapshot);
            if (cursorPos < 0) yield break;

            ITextSnapshotLine line = snapshot.GetLineFromPosition(cursorPos);
            int lineEnd = line.End.Position;
            if (lineEnd <= cursorPos) yield break;

            var hideSpan = new SnapshotSpan(snapshot, cursorPos, lineEnd - cursorPos);

            foreach (SnapshotSpan requested in spans)
            {
                if (requested.IntersectsWith(hideSpan))
                {
                    // Zero-width invisible element replaces the post-cursor text visually
                    // without touching the buffer. Some editor versions render the original
                    // text when the adornment is null, so we use an explicit empty Border.
                    var hidden = new Border
                    {
                        Width = 0,
                        Height = 0,
                        IsHitTestVisible = false,
                    };
                    yield return new TagSpan<IntraTextAdornmentTag>(
                        hideSpan,
                        new IntraTextAdornmentTag(hidden, removalCallback: null));
                    yield break;
                }
            }
        }

        private void OnSuggestionStateChanged(object sender, SuggestionStateChangedEventArgs e)
        {
            ITextSnapshot snapshot = _view.TextSnapshot;
            SnapshotSpan? span = e.AffectedSpan;
            if (span.HasValue)
            {
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(span.Value));
            }
            else
            {
                var fullSpan = new SnapshotSpan(snapshot, 0, snapshot.Length);
                TagsChanged?.Invoke(this, new SnapshotSpanEventArgs(fullSpan));
            }
        }

        public void Dispose()
        {
            _session.SuggestionStateChanged -= OnSuggestionStateChanged;
        }
    }
}
