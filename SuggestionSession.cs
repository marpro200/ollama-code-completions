using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Text.Formatting;

namespace OllamaCopilot
{
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
        private bool _suppressBufferEvent;   // set while applying our own edits
        private string _lastSeenModel;       // for detecting model changes that require a cache clear

        private SuggestionSession(IWpfTextView view)
        {
            _view = view;
            _view.TextBuffer.Changed += OnTextBufferChanged;
            _view.Caret.PositionChanged += OnCaretPositionChanged;
            _view.LayoutChanged += OnLayoutChanged;
            _view.Closed += OnClosed;
        }

        public bool HasActiveSuggestion => !string.IsNullOrEmpty(_suggestion);

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
                await StatusBar.SetAsync("Ollama Copilot: " + ex.Message).ConfigureAwait(false);
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
            string lineAfterCursor  = snapshot.GetText(caret, caretLine.End.Position - caret);

            // Don't send empty-prefix-and-empty-suffix requests (cursor in totally empty file).
            if (prefix.Length == 0 && suffix.Length == 0) return;

            ITrackingPoint anchor = snapshot.CreateTrackingPoint(caret, PointTrackingMode.Negative);

            // Cache check — we may have arrived here via the debounce timer after the
            // synchronous check in OnTextBufferChanged already missed; check again in case
            // something was stored in the interim.
            string cachedCompletion = _cache.TryGetExact(prefix, suffix)
                                   ?? _cache.TryGetByExtension(prefix, suffix);
            if (cachedCompletion != null)
            {
                ShowSuggestion(anchor, cachedCompletion);
                return;
            }

            string username = null, password = null;
            if (opts.UseAuthentication)
            {
                var creds = CredentialStorage.Read();
                username = creds.Username;
                password = creds.Password;
            }

            await StatusBar.SetAsync("Ollama Copilot: thinking…").ConfigureAwait(false);

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
            completion = CompletionPostProcessor.Clean(completion, lineBeforeCursor, lineAfterCursor, suffix);
            if (completion == null) return;

            // Store the post-processed completion before switching back to the UI thread.
            // The cache is thread-safe so calling Store here is fine.
            _cache.Store(prefix, suffix, completion);

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
            EnsureLayer();
            RedrawGhost();
        }

        private void EnsureLayer()
        {
            if (_layer == null)
                _layer = _view.GetAdornmentLayer(TextViewListener.GhostTextLayerName);
        }

        private void RedrawGhost()
        {
            if (_layer == null || _anchor == null || string.IsNullOrEmpty(_suggestion)) return;

            _layer.RemoveAllAdornments();

            int pos = _anchor.GetPosition(_view.TextSnapshot);
            if (pos > _view.TextSnapshot.Length) return;
            var anchorPoint = new SnapshotPoint(_view.TextSnapshot, pos);

            ITextViewLine line;
            try
            {
                line = _view.GetTextViewLineContainingBufferPosition(anchorPoint);
            }
            catch
            {
                return;
            }
            if (line == null) return;

            // Use the editor's typeface so ghost text aligns with real code.
            var typeface = _view.FormattedLineSource.DefaultTextProperties.Typeface;
            double emSize = _view.FormattedLineSource.DefaultTextProperties.FontRenderingEmSize;
            var brush = new SolidColorBrush(Color.FromArgb(140, 150, 150, 150));
            brush.Freeze();

            string[] lines = _suggestion.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);
            double firstLeft = line.GetCharacterBounds(anchorPoint).Left;
            double restLeft = line.TextLeft;     // start of the editor's text area on this line
            double top = line.TextTop;
            double lineHeight = line.Height;

            // Container Canvas so all lines move together if anything reflows.
            var canvas = new Canvas
            {
                IsHitTestVisible = false,
                Background = null,
            };

            for (int i = 0; i < lines.Length; i++)
            {
                var tb = new TextBlock
                {
                    Text = lines[i],
                    Foreground = brush,
                    FontFamily = typeface.FontFamily,
                    FontStyle = typeface.Style,
                    FontWeight = typeface.Weight,
                    FontStretch = typeface.Stretch,
                    FontSize = emSize,
                    IsHitTestVisible = false,
                    TextWrapping = TextWrapping.NoWrap,
                };
                Canvas.SetLeft(tb, i == 0 ? firstLeft : restLeft);
                Canvas.SetTop(tb, top + i * lineHeight);
                canvas.Children.Add(tb);
            }

            _layer.AddAdornment(
                AdornmentPositioningBehavior.TextRelative,
                new SnapshotSpan(anchorPoint, 0),
                tag: null,
                adornment: canvas,
                removedCallback: null);
        }

        public void DismissSuggestion()
        {
            _suggestion = null;
            _anchor = null;
            _layer?.RemoveAllAdornments();
        }

        /// <summary>
        /// Inserts the ghosted text into the buffer at the anchor and moves
        /// the caret to the end of the inserted span.
        /// </summary>
        public bool AcceptSuggestion()
        {
            if (!HasActiveSuggestion || _anchor == null) return false;

            string text = _suggestion;
            int pos = _anchor.GetPosition(_view.TextSnapshot);

            // Clear visual state first so the buffer-changed handler doesn't
            // try to redraw stale state.
            DismissSuggestion();

            _suppressBufferEvent = true;
            try
            {
                using (var edit = _view.TextBuffer.CreateEdit())
                {
                    edit.Insert(pos, text);
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
            int suffixEnd   = Math.Min(snapshot.Length, caret + Math.Max(0, opts.MaxSuffixChars));
            string prefix = snapshot.GetText(prefixStart, caret - prefixStart);
            string suffix = snapshot.GetText(caret, suffixEnd - caret);

            string cached = _cache.TryGetExact(prefix, suffix)
                         ?? _cache.TryGetByExtension(prefix, suffix);
            if (cached == null) return false;

            ITrackingPoint anchor = snapshot.CreateTrackingPoint(caret, PointTrackingMode.Negative);
            ShowSuggestion(anchor, cached);
            return true;
        }

        /// <summary>Clears the cache when the model has changed since the last request.</summary>
        private void CheckModelChange(OptionsPage opts)
        {
            string model = opts?.Model ?? string.Empty;
            if (_lastSeenModel != null && _lastSeenModel != model)
                _cache.Clear();
            _lastSeenModel = model;
        }

        // ---------------------- options access ----------------------

        private static OptionsPage TryGetOptions()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            try
            {
                if (OllamaCopilotPackage.Instance != null)
                    return OllamaCopilotPackage.Instance.Options;

                // Force-load the package if MEF beat the auto-load.
                if (Package.GetGlobalService(typeof(SVsShell)) is IVsShell shell)
                {
                    var pkgGuid = new Guid(OllamaCopilotPackage.PackageGuidString);
                    shell.LoadPackage(ref pkgGuid, out _);
                }
                return OllamaCopilotPackage.Instance?.Options;
            }
            catch
            {
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
    }
}
