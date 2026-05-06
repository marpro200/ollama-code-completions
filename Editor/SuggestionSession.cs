using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;
using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace OllamaCodeCompletions
{
    internal enum InsertionMode
    {
        InsertAtCursor,
        ReplaceToEndOfLine,
    }

    internal sealed class SuggestionStateChangedEventArgs : EventArgs
    {
        public SnapshotSpan? AffectedSpan { get; }
        public SuggestionStateChangedEventArgs(SnapshotSpan? affected) { AffectedSpan = affected; }
    }

    /// <summary>
    /// Owns the inline-suggestion lifecycle for one editor view:
    ///
    ///   keystroke → invalidate current suggestion
    ///             → debounce N ms
    ///             → snapshot prefix/suffix
    ///             → call Ollama (cancellable)
    ///             → if caret hasn't moved: render ghost text
    ///
    /// Tab accepts; Esc dismisses (handled by <see cref="CommandFilter"/>, which
    /// calls back into this session).
    /// </summary>
    internal sealed class SuggestionSession
    {
        private static readonly object s_propertyKey = new object();

        public static SuggestionSession GetOrCreate(IWpfTextView view)
        {
            return view.Properties.GetOrCreateSingletonProperty(s_propertyKey, () => new SuggestionSession(view));
        }

        private readonly IWpfTextView _view;
        private readonly OllamaClient _client = new OllamaClient();
        private readonly CompletionCache _cache = new CompletionCache();

        private CancellationTokenSource _cts;
        private IAdornmentLayer _layer;
        private string _suggestion;          // raw text returned by Ollama (may be multi-line)
        private ITrackingPoint _anchor;      // tracks the cursor position the suggestion is for
        private InsertionMode _insertionMode;
        private bool _suppressBufferEvent;   // set while applying our own edits
        private string _lastSeenModel;       // for detecting model changes that require a cache clear
        private int _extraLineCount;         // lines 2..N of the current suggestion (0 for single-line)

        public event EventHandler<SuggestionStateChangedEventArgs> SuggestionStateChanged;

        private SuggestionSession(IWpfTextView view)
        {
            _view = view;
            _view.TextBuffer.Changed += OnTextBufferChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;
        }

        public bool HasActiveSuggestion => !string.IsNullOrEmpty(_suggestion);
        public InsertionMode CurrentInsertionMode => _insertionMode;

        public int GetAnchorPosition(ITextSnapshot snapshot)
        {
            return _anchor?.GetPosition(snapshot) ?? -1;
        }

        // ---------------------- event handlers ----------------------

        private void OnTextBufferChanged(object sender, TextContentChangedEventArgs e)
        {
            if (_suppressBufferEvent) return;

            // Any user edit invalidates whatever suggestion was on screen.
            DismissSuggestion();

            if (!IsExtensionEnabled()) return;

            // Cancel any in-flight request.
            _cts?.Cancel();
            _cts = null;

            // Fast path: serve instantly from cache — no debounce, no network call.
            if (TryShowFromCache()) return;

            // Cache miss — start a debounced Ollama request.
            _cts = new CancellationTokenSource();
            _ = ScheduleAsync(SafeGetDebounceMs(), _cts.Token);
        }

        private void OnCaretPositionChanged(object sender, CaretPositionChangedEventArgs e)
        {
            if (!HasActiveSuggestion || _anchor == null) return;

            // If the caret moved off the anchor (other than by our own accept), drop the suggestion.
            int anchorPos = _anchor.GetPosition(_view.TextSnapshot);
            int caretPos = e.NewPosition.BufferPosition.Position;
            if (anchorPos != caretPos) DismissSuggestion();
        }

        private void OnLayoutChanged(object sender, TextViewLayoutChangedEventArgs e)
        {
            if (HasActiveSuggestion) RedrawGhost();
        }

        private void OnClosed(object sender, EventArgs e)
        {
            _cts?.Cancel();
            _view.TextBuffer.Changed -= OnTextBufferChanged;
            _view.Caret.PositionChanged -= OnCaretPositionChanged;
            _view.LayoutChanged -= OnLayoutChanged;
            _view.Closed -= OnClosed;
        }

        // ---------------------- request flow ----------------------

        private async Task ScheduleAsync(int debounceMs, CancellationToken ct)
        {
            try
            {
                if (debounceMs > 0)
                    await Task.Delay(debounceMs, ct).ConfigureAwait(false);
                await RequestSuggestionAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected — user kept typing.
            }
            catch (Exception ex)
            {
                // Top-level boundary for fire-and-forget. Without a final catch, any
                // unexpected exception escaping RequestSuggestionAsync goes silently
                // to TaskScheduler.UnobservedTaskException — neither logged nor surfaced.
                // Log first, then surface in the status bar.
                Logger.LogException("Error", ex);
                await StatusBar.SetAsync("Ollama Code Completions: " + ex.Message).ConfigureAwait(false);
            }
        }

        private async Task RequestSuggestionAsync(CancellationToken ct)
        {
            // Snapshot context on the UI thread.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            if (_view.IsClosed) return;

            OptionsPage opts = TryGetOptions();
            if (opts == null || !opts.Enabled) return;

            // Clear the cache when the model changes so stale completions are not shown.
            CheckModelChange(opts);

            ITextSnapshot snapshot = _view.TextSnapshot;
            int caret = _view.Caret.Position.BufferPosition.Position;
            int prefixStart = Math.Max(0, caret - Math.Max(0, opts.MaxPrefixChars));
            int suffixEnd = Math.Min(snapshot.Length, caret + Math.Max(0, opts.MaxSuffixChars));
            string prefix = snapshot.GetText(prefixStart, caret - prefixStart);
            string suffix = snapshot.GetText(caret, suffixEnd - caret);

            ITextSnapshotLine caretLine = snapshot.GetLineFromPosition(caret);
            string lineBeforeCursor = snapshot.GetText(caretLine.Start.Position, caret - caretLine.Start.Position);
            string lineAfterCursor = snapshot.GetText(caret, caretLine.End.Position - caret);

            // Don't send empty-prefix-and-empty-suffix requests (cursor in totally empty file).
            if (prefix.Length == 0 && suffix.Length == 0) return;

            ITrackingPoint anchor = snapshot.CreateTrackingPoint(caret, PointTrackingMode.Negative);

            // Prepend file header so the model knows the language and project layout.
            string fileHeader = FileHeaderBuilder.TryBuildFileHeader(_view);
            if (!string.IsNullOrEmpty(fileHeader))
                prefix = fileHeader + prefix;

            Logger.Log("Request", $"caret={caret} prefix={prefix.Length}ch suffix={suffix.Length}ch model={opts.Model}");

            // Cache check — we may have arrived here via the debounce timer after the
            // synchronous check in OnTextBufferChanged already missed; check again in case
            // something was stored in the interim.
            string cachedExact = _cache.TryGetExact(prefix, suffix);
            if (cachedExact != null)
            {
                Logger.Log("Cache", "HIT (exact)");
                ShowSuggestion(anchor, cachedExact);
                return;
            }
            string cachedExt = _cache.TryGetByExtension(prefix, suffix);
            if (cachedExt != null)
            {
                Logger.Log("Cache", "HIT (extension)");
                ShowSuggestion(anchor, cachedExt);
                return;
            }
            Logger.Log("Cache", "MISS");

            string username = null, password = null;
            if (opts.UseAuthentication)
            {
                var creds = CredentialStorage.Read();
                username = creds.Username;
                password = creds.Password;
            }

            await StatusBar.SetAsync("Ollama Code Completions: thinking…").ConfigureAwait(false);

            string completion;
            try
            {
                completion = await _client.GetCompletionAsync(new OllamaClient.CompletionRequest
                {
                    ServerUrl = opts.ServerUrl,
                    Model = opts.Model,
                    Prefix = prefix,
                    Suffix = suffix,
                    MaxPredict = opts.MaxPredict,
                    TimeoutSeconds = opts.TimeoutSeconds,
                    UseAuth = opts.UseAuthentication,
                    Username = username,
                    Password = password,
                }, ct).ConfigureAwait(false);
            }
            finally
            {
                await StatusBar.ClearAsync().ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            // Post-process on the background thread — pure CPU work, no UI access needed.
            int rawLen = completion?.Length ?? 0;
            completion = CompletionPostProcessor.Clean(completion, lineBeforeCursor, lineAfterCursor, suffix);
            Logger.Log("PostProcess", $"in={rawLen}ch out={completion?.Length ?? 0}ch");
            if (completion == null) return;

            // Store the post-processed completion before switching back to the UI thread.
            // The cache is thread-safe so calling Store here is fine.
            _cache.Store(prefix, suffix, completion);
            Logger.Log("Cache", $"STORE (length={completion.Length}ch)");

            // Back to UI to render — re-validate that the world hasn't moved.
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(ct);
            if (_view.IsClosed) return;
            int currentCaret = _view.Caret.Position.BufferPosition.Position;
            int anchorNow = anchor.GetPosition(_view.TextSnapshot);
            if (currentCaret != anchorNow) return;

            ShowSuggestion(anchor, completion);
        }

        // ---------------------- rendering ----------------------

        private void ShowSuggestion(ITrackingPoint anchor, string text)
        {
            _suggestion = text;
            _anchor = anchor;

            // Determine whether the cursor is mid-line so accept knows whether to replace
            // the rest of the line or insert at the cursor.
            int anchorPos = anchor.GetPosition(_view.TextSnapshot);
            ITextSnapshotLine anchorLine = _view.TextSnapshot.GetLineFromPosition(anchorPos);
            string textAfterAnchor = _view.TextSnapshot.GetText(anchorPos, anchorLine.End.Position - anchorPos);
            _insertionMode = HasNonWhitespaceAfterCursor(textAfterAnchor)
                ? InsertionMode.ReplaceToEndOfLine
                : InsertionMode.InsertAtCursor;

            EnsureLayer();

            string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            int newExtraLineCount = Math.Max(0, lines.Length - 1);

            Logger.Log("Render", $"length={text.Length}ch lines={lines.Length} gap={newExtraLineCount}");

            if (newExtraLineCount != _extraLineCount)
            {
                _extraLineCount = newExtraLineCount;
                // Force the layout engine to re-query GetLineTransform for the anchor
                // line so it allocates (or releases) the extra BottomSpace.  The
                // resulting LayoutChanged event will call RedrawGhost.
                InvalidateAnchorLineTransform();
            }
            else
            {
                RedrawGhost();
            }

            SnapshotSpan? span = ComputeAffectedSpan();
            SuggestionStateChanged?.Invoke(this, new SuggestionStateChangedEventArgs(span));
        }

        private SnapshotSpan? ComputeAffectedSpan()
        {
            if (_anchor == null) return null;
            try
            {
                ITextSnapshot snapshot = _view.TextSnapshot;
                int pos = _anchor.GetPosition(snapshot);
                ITextSnapshotLine line = snapshot.GetLineFromPosition(pos);
                return new SnapshotSpan(snapshot, pos, line.End.Position - pos);
            }
            catch (ArgumentException ex)
            {
                Logger.LogException("Render", ex);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogException("Render", ex);
                return null;
            }
        }

        private void EnsureLayer()
        {
            if (_layer != null) return;
            try
            {
                _layer = _view.GetAdornmentLayer(TextViewListener.GhostTextLayerName);
            }
            catch (Exception ex)
            {
                // GetAdornmentLayer throws if no matching definition exists for this view.
                // Bare catch is justified: this is the must-not-throw boundary documented
                // in the 1.0.2 hotfix. Leave _layer null; RedrawGhost guards against it.
                Logger.LogException("Render", ex);
            }
        }

        private void RedrawGhost()
        {
            if (_layer == null || _anchor == null || string.IsNullOrEmpty(_suggestion)) return;
            try
            {
                _layer.RemoveAllAdornments();

                int pos = _anchor.GetPosition(_view.TextSnapshot);
                if (pos > _view.TextSnapshot.Length) return;
                var anchorPoint = new SnapshotPoint(_view.TextSnapshot, pos);

                ITextViewLine line = _view.GetTextViewLineContainingBufferPosition(anchorPoint);
                if (line == null) return;

                // Use the editor's typeface so ghost text aligns with real code.
                var typeface = _view.FormattedLineSource.DefaultTextProperties.Typeface;
                double emSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize;

                var brush = new SolidColorBrush(Color.FromArgb(140, 150, 150, 150));
                brush.Freeze();

                string[] lines = _suggestion.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
                double firstLeft = line.GetCharacterBounds(anchorPoint).Left;
                double restLeft = line.TextLeft;

                // The ILineTransformSource has already allocated (lines.Length - 1) * TextHeight
                // of extra BottomSpace below the anchor line's text.  Place line 0 at the normal
                // text row and lines 1..N inside that reserved gap — never at real-code positions.
                double rowHeight = line.TextHeight;
                var positions = new (double Left, double Top, double Height)[lines.Length];
                positions[0] = (firstLeft, line.TextTop, rowHeight);
                for (int i = 1; i < lines.Length; i++)
                {
                    positions[i] = (restLeft, line.TextBottom + (i - 1) * rowHeight, rowHeight);
                }

                // Container Canvas so all lines move together if anything reflows.
                var canvas = new Canvas
                {
                    IsHitTestVisible = false,
                    Background = null,
                };

                // blue bar to the left of the suggestion so we can be 100% sure
                // the ghost text is ours and not from IntelliCode/Copilot/etc.
                // Spans from the top of the first rendered line to the bottom of the last.
                var markerBrush = new SolidColorBrush(Color.FromRgb(0, 138, 203));
                markerBrush.Freeze();
                double markerTop = positions[0].Top + 1;
                double markerBottom = positions[positions.Length - 1].Top + positions[positions.Length - 1].Height - 1;
                var marker = new System.Windows.Shapes.Rectangle
                {
                    Width = 3,
                    Height = Math.Max(1, markerBottom - markerTop),
                    Fill = markerBrush,
                    IsHitTestVisible = false,
                };
                Canvas.SetLeft(marker, _view.ViewportLeft + 2);
                Canvas.SetTop(marker, markerTop);
                canvas.Children.Add(marker);

                for (int i = 0; i < lines.Length; i++)
                {
                    var tb = new TextBlock
                    {
                        Text = lines[i],
                        Foreground = brush,
                        FontFamily = typeface.FontFamily,
                        FontStyle = FontStyles.Italic,
                        FontWeight = typeface.Weight,
                        FontStretch = typeface.Stretch,
                        FontSize = emSize,
                        IsHitTestVisible = false,
                        TextWrapping = TextWrapping.NoWrap,
                    };
                    Canvas.SetLeft(tb, positions[i].Left);
                    Canvas.SetTop(tb, positions[i].Top);
                    canvas.Children.Add(tb);
                }

                _layer.AddAdornment(
                    AdornmentPositioningBehavior.TextRelative,
                    new SnapshotSpan(anchorPoint, 0),
                    tag: null,
                    adornment: canvas,
                    removedCallback: null);
            }
            catch (Exception ex)
            {
                // Bare catch is justified: this is the must-not-throw boundary documented
                // in the 1.0.2 hotfix. Called from OnLayoutChanged on the UI thread, an
                // escaping exception would propagate into the editor's layout pass.
                Logger.LogException("Render", ex);
            }
        }

        public void DismissSuggestion()
        {
            // Compute affected span before clearing state — _anchor is needed.
            SnapshotSpan? affectedSpan = ComputeAffectedSpan();

            int prevExtraLineCount = _extraLineCount;
            ITrackingPoint prevAnchor = _anchor;

            _suggestion = null;
            _anchor = null;
            _insertionMode = InsertionMode.InsertAtCursor;
            _extraLineCount = 0;
            _layer?.RemoveAllAdornments();

            // Release the reserved vertical space so the layout engine stops pushing
            // real lines down below the old anchor.
            if (prevExtraLineCount > 0 && prevAnchor != null)
                InvalidateAnchorLineTransformAt(prevAnchor);

            // Fire after state is cleared so GetTags sees no active suggestion and emits no tag.
            SuggestionStateChanged?.Invoke(this, new SuggestionStateChangedEventArgs(affectedSpan));
        }

        // ---------------------- line transform support ----------------------

        /// <summary>
        /// Called by <see cref="GhostTextLineTransformSource"/> for every line during
        /// layout.  Returns the number of extra suggestion lines that need vertical
        /// space below <paramref name="line"/>, or 0 if this line is not the anchor.
        /// </summary>
        internal int GetExtraLinesFor(ITextViewLine line)
        {
            if (_extraLineCount <= 0 || _anchor == null) return 0;
            try
            {
                int anchorPos = _anchor.GetPosition(_view.TextSnapshot);
                if (line.ContainsBufferPosition(new SnapshotPoint(_view.TextSnapshot, anchorPos)))
                    return _extraLineCount;
            }
            catch (ArgumentException ex)
            {
                // Tracking-point GetPosition can throw if the snapshot is mid-replace.
                Logger.LogException("Render", ex);
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogException("Render", ex);
            }
            return 0;
        }

        private void InvalidateAnchorLineTransform()
            => InvalidateAnchorLineTransformAt(_anchor);

        private void InvalidateAnchorLineTransformAt(ITrackingPoint anchor)
        {
            if (anchor == null) return;
            try
            {
                int pos = anchor.GetPosition(_view.TextSnapshot);
                var anchorPoint = new SnapshotPoint(_view.TextSnapshot, pos);
                ITextViewLine anchorLine = _view.GetTextViewLineContainingBufferPosition(anchorPoint);
                if (anchorLine == null) return;
                // Keep the line at its current viewport-relative y position; the layout
                // engine will re-query GetLineTransform and adjust the bottom space.
                _view.DisplayTextLineContainingBufferPosition(
                    anchorPoint,
                    anchorLine.Top - _view.ViewportTop,
                    ViewRelativePosition.Top);
            }
            catch (ArgumentException ex)
            {
                Logger.LogException("Render", ex);
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogException("Render", ex);
            }
        }

        // ---------------------- accept ----------------------

        /// <summary>
        /// Inserts the ghosted text into the buffer at the anchor and moves
        /// the caret to the end of the inserted span. In ReplaceToEndOfLine mode,
        /// the existing text from the anchor to end-of-line is replaced.
        /// </summary>
        public bool AcceptSuggestion()
        {
            if (!HasActiveSuggestion || _anchor == null) return false;

            string text = _suggestion;
            InsertionMode mode = _insertionMode;
            int pos = _anchor.GetPosition(_view.TextSnapshot);

            // Clear visual state first so the buffer-changed handler doesn't
            // try to redraw stale state.
            DismissSuggestion();

            _suppressBufferEvent = true;
            try
            {
                using (var edit = _view.TextBuffer.CreateEdit())
                {
                    if (mode == InsertionMode.ReplaceToEndOfLine)
                    {
                        ITextSnapshotLine currentLine = _view.TextSnapshot.GetLineFromPosition(pos);
                        edit.Replace(pos, currentLine.End.Position - pos, text);
                    }
                    else
                    {
                        edit.Insert(pos, text);
                    }
                    edit.Apply();
                }
                int newPos = pos + text.Length;
                if (newPos <= _view.TextSnapshot.Length)
                {
                    _view.Caret.MoveTo(new SnapshotPoint(_view.TextSnapshot, newPos));
                }
            }
            finally
            {
                _suppressBufferEvent = false;
            }
            return true;
        }

        // ---------------------- cache helpers ----------------------

        /// <summary>
        /// Tries to serve a completion from the cache synchronously on the UI thread.
        /// Returns true (and renders immediately) on a cache hit; false on a miss.
        /// </summary>
        private bool TryShowFromCache()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_view.IsClosed) return false;

            OptionsPage opts = TryGetOptions();
            if (opts == null || !opts.Enabled) return false;

            CheckModelChange(opts);

            ITextSnapshot snapshot = _view.TextSnapshot;
            int caret = _view.Caret.Position.BufferPosition.Position;
            int prefixStart = Math.Max(0, caret - Math.Max(0, opts.MaxPrefixChars));
            int suffixEnd = Math.Min(snapshot.Length, caret + Math.Max(0, opts.MaxSuffixChars));
            string prefix = snapshot.GetText(prefixStart, caret - prefixStart);
            string suffix = snapshot.GetText(caret, suffixEnd - caret);

            // Match the header prepended by RequestSuggestionAsync so cache keys align.
            string fileHeader = FileHeaderBuilder.TryBuildFileHeader(_view);
            if (!string.IsNullOrEmpty(fileHeader))
                prefix = fileHeader + prefix;

            string cachedExact = _cache.TryGetExact(prefix, suffix);
            string cached = cachedExact;
            string hitKind = "exact";
            if (cached == null)
            {
                cached = _cache.TryGetByExtension(prefix, suffix);
                hitKind = "extension";
            }
            if (cached == null) return false;

            Logger.Log("Cache", $"HIT ({hitKind}) [sync]");
            ITrackingPoint anchor = snapshot.CreateTrackingPoint(caret, PointTrackingMode.Negative);
            ShowSuggestion(anchor, cached);
            return true;
        }

        /// <summary>Clears the cache when the model has changed since the last request.</summary>
        private void CheckModelChange(OptionsPage opts)
        {
            string model = opts?.Model ?? string.Empty;
            if (_lastSeenModel != null && _lastSeenModel != model)
            {
                _cache.Clear();
                Logger.Log("Cache", $"CLEAR (model changed: {_lastSeenModel} -> {model})");
                Logger.Log("Options", $"model changed: {_lastSeenModel} -> {model}");
            }
            _lastSeenModel = model;
        }

        // ---------------------- options access ----------------------

        private static OptionsPage TryGetOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (OllamaCodeCompletionsPackage.Instance != null)
                    return OllamaCodeCompletionsPackage.Instance.Options;

                // Force-load the package if MEF beat the auto-load.
                if (Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell)
                {
                    var pkgGuid = new Guid(OllamaCodeCompletionsPackage.PackageGuidString);
                    shell.LoadPackage(ref pkgGuid, out _);
                }
                return OllamaCodeCompletionsPackage.Instance?.Options;
            }
            catch (COMException ex)
            {
                Logger.LogException("Options", ex);
                return null;
            }
            catch (InvalidOperationException ex)
            {
                Logger.LogException("Options", ex);
                return null;
            }
        }

        private static bool IsExtensionEnabled()
        {
            var o = TryGetOptions();
            return o != null && o.Enabled;
        }

        private static int SafeGetDebounceMs()
        {
            var o = TryGetOptions();
            int ms = o?.DebounceMs ?? 300;
            return Math.Max(0, ms);
        }

        // ---------------------- mid-line helpers ----------------------

        private static bool HasNonWhitespaceAfterCursor(string lineAfterCursor)
            => !string.IsNullOrWhiteSpace(lineAfterCursor);
    }
}
