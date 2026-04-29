using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.VisualStudio.Shell;

namespace OllamaCopilot
{
    /// <summary>
    /// Tools → Options → Ollama Copilot → General.
    ///
    /// Most properties are auto-persisted by VS's <see cref="DialogPage"/>.
    /// <see cref="Username"/> and <see cref="Password"/> are NOT persisted that way —
    /// their getters/setters round-trip through <see cref="CredentialStorage"/> so
    /// secrets never end up in the user's settings XML.
    /// </summary>
    [Guid("a7e2f8b3-9c14-4f2d-8e6b-1f5d3c9a8e72")]
    [ComVisible(true)]
    public class OptionsPage : DialogPage
    {
        private const string CategoryConnection = "Connection";
        private const string CategoryAuthentication = "Authentication";
        private const string CategoryBehavior = "Behavior";
        private const string PasswordSentinel = "********";

        // --- Connection ---

        [Category(CategoryConnection)]
        [DisplayName("Server URL")]
        [Description("Base URL of the Ollama server, e.g. https://ollama.yourdomain.com (no trailing /api/generate).")]
        public string ServerUrl { get; set; } = "http://localhost:11434";

        [Category(CategoryConnection)]
        [DisplayName("Model")]
        [Description("Model name to use for FIM completion, e.g. qwen2.5-coder:1.5b.")]
        public string Model { get; set; } = "qwen2.5-coder:1.5b";

        // --- Authentication ---

        [Category(CategoryAuthentication)]
        [DisplayName("Use HTTP Basic authentication")]
        [Description("If enabled, sends an Authorization: Basic <base64> header on every Ollama API request.")]
        public bool UseAuthentication { get; set; } = false;

        [Category(CategoryAuthentication)]
        [DisplayName("Username")]
        [Description("HTTP Basic auth username. Stored alongside the password in the Windows Credential Manager.")]
        public string Username
        {
            get => CredentialStorage.GetUsername() ?? string.Empty;
            set => CredentialStorage.Save(value ?? string.Empty, CredentialStorage.GetPassword() ?? string.Empty);
        }

        [Category(CategoryAuthentication)]
        [DisplayName("Password")]
        [Description("HTTP Basic auth password. Stored securely in the Windows Credential Manager (target: OllamaCopilot:Auth).")]
        [PasswordPropertyText(true)]
        public string Password
        {
            // Show a sentinel rather than the real password so it never leaves the credential store
            // (the property grid copies the value, and could log it).
            get => string.IsNullOrEmpty(CredentialStorage.GetPassword()) ? string.Empty : PasswordSentinel;
            set
            {
                if (value == PasswordSentinel) return;       // unchanged in the dialog
                CredentialStorage.Save(CredentialStorage.GetUsername() ?? string.Empty, value ?? string.Empty);
            }
        }

        // --- Behavior ---

        [Category(CategoryBehavior)]
        [DisplayName("Enabled")]
        [Description("Globally enable or disable inline suggestions.")]
        public bool Enabled { get; set; } = true;

        [Category(CategoryBehavior)]
        [DisplayName("Debounce delay (ms)")]
        [Description("Idle time after the last keystroke before requesting a suggestion. Default: 300 ms.")]
        public int DebounceMs { get; set; } = 300;

        [Category(CategoryBehavior)]
        [DisplayName("Max prefix characters")]
        [Description("Maximum number of characters before the cursor sent as context.")]
        public int MaxPrefixChars { get; set; } = 4096;

        [Category(CategoryBehavior)]
        [DisplayName("Max suffix characters")]
        [Description("Maximum number of characters after the cursor sent as context.")]
        public int MaxSuffixChars { get; set; } = 1024;

        [Category(CategoryBehavior)]
        [DisplayName("Max tokens to predict")]
        [Description("Upper bound on tokens to generate per suggestion (Ollama num_predict).")]
        public int MaxPredict { get; set; } = 128;

        [Category(CategoryBehavior)]
        [DisplayName("Request timeout (seconds)")]
        [Description("HTTP timeout for each Ollama request.")]
        public int TimeoutSeconds { get; set; } = 30;
    }
}
