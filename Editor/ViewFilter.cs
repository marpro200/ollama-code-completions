using Microsoft.VisualStudio.Text;
using Microsoft.VisualStudio.Text.Editor;
using Microsoft.VisualStudio.Utilities;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Centralised gate for "should this view get a SuggestionSession attached?".
    ///
    /// Every MEF entry point that can reach <see cref="SuggestionSession.GetOrCreate"/>
    /// must call this before doing so. The TextViewRole MEF attributes are the primary
    /// defense (filtered at activation), but they use OR semantics across multiple
    /// [TextViewRole] attributes — Output / Immediate / Find Results windows still
    /// match Interactive/Editable. ShouldAttach is the runtime backstop that requires
    /// BOTH Document AND PrimaryDocument roles AND a real ITextDocument.
    /// </summary>
    internal static class ViewFilter
    {
        public static bool ShouldAttach(IWpfTextView view, string site)
        {
            if (view == null)
            {
                Logger.Log("Attach", $"{site}: view=null -> SKIP");
                return false;
            }

            string contentType = view.TextBuffer.ContentType?.TypeName ?? "(unknown)";
            string roles = string.Join(",", view.Roles);
            bool hasDoc = view.TextBuffer.Properties.TryGetProperty(typeof(ITextDocument), out ITextDocument _);
            bool hasDocRole = view.Roles.Contains(PredefinedTextViewRoles.Document);
            bool hasPrimary = view.Roles.Contains(PredefinedTextViewRoles.PrimaryDocument);
            bool isClosed = view.IsClosed;

            bool ok = !isClosed && hasDocRole && hasPrimary && hasDoc;

            Logger.Log("Attach",
                $"{site}: contentType={contentType} roles=[{roles}] hasDoc={hasDoc} -> {(ok ? "ATTACH" : "SKIP")}");

            return ok;
        }
    }
}
