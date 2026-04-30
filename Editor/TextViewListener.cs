using System.ComponentModel.Composition;
using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// MEF entry point for WPF text views. Two responsibilities:
    /// 1. Declare the named adornment layer that ghost text is drawn on.
    /// 2. Attach a <see cref="SuggestionSession"/> to every editable text view.
    /// </summary>
    [Export(typeof(IWpfTextViewCreationListener))]
    [ContentType("code")]
    [ContentType("text")]
    [TextViewRole(PredefinedTextViewRoles.Document)]
    [TextViewRole(PredefinedTextViewRoles.PrimaryDocument)]
    [TextViewRole(PredefinedTextViewRoles.Editable)]
    [TextViewRole(PredefinedTextViewRoles.Interactive)]
    internal sealed class TextViewListener : IWpfTextViewCreationListener
    {
        public const string GhostTextLayerName = "OllamaCopilot.GhostText";

        // Layer order: drawn after the caret so ghost text doesn't get clipped behind it.
        [Export(typeof(AdornmentLayerDefinition))]
        [Name(GhostTextLayerName)]
        [Order(After = PredefinedAdornmentLayers.Caret)]
        [TextViewRole(PredefinedTextViewRoles.Document)]
#pragma warning disable CS0649 // assigned by MEF
        public AdornmentLayerDefinition GhostTextLayerDefinition;
#pragma warning restore CS0649

        public void TextViewCreated(IWpfTextView textView)
        {
            string contentType = textView.TextBuffer.ContentType?.TypeName ?? "(unknown)";
            string roles = string.Join(",", textView.Roles);

            if (!textView.TextBuffer.Properties.TryGetProperty(
                    typeof(ITextDocument), out ITextDocument doc))
            {
                Logger.Log("Attach", $"skip (no ITextDocument) contentType={contentType} roles=[{roles}]");
                return;
            }

            Logger.Log("Attach", $"contentType={contentType} roles=[{roles}] file={doc.FilePath ?? "(unsaved)"}");

            // Lazy-create the per-view session so it's wired to text/caret events.
            SuggestionSession.GetOrCreate(textView);
        }
    }
}
