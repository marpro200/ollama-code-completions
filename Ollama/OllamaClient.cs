using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OllamaCodeCompletions
{
    /// <summary>
    /// Minimal client for Ollama's <c>/api/generate</c> endpoint, using its native
    /// <c>suffix</c> parameter for fill-in-the-middle prompting. The endpoint applies
    /// the right FIM tokens for whichever code model is configured (qwen2.5-coder,
    /// codellama, codegemma, deepseek-coder, …) so we don't have to hard-code them.
    /// </summary>
    internal sealed class OllamaClient
    {
        private static readonly HttpClient s_http = new HttpClient
        {
            // Per-request timeouts are still enforced via the CancellationToken; this is just a
            // hard upper bound to prevent stuck connections from leaking forever.
            Timeout = TimeSpan.FromMinutes(2),
        };

        public sealed class CompletionRequest
        {
            public string ServerUrl { get; set; }
            public string Model { get; set; }
            public string Prefix { get; set; }
            public string Suffix { get; set; }
            public int MaxPredict { get; set; } = 128;
            public int TimeoutSeconds { get; set; } = 30;
            public bool UseAuth { get; set; }
            public string Username { get; set; }
            public string Password { get; set; }
        }

        /// <summary>
        /// Returns the suggested completion text, or null if the request failed,
        /// was cancelled, or the server returned an empty response. Never throws.
        /// </summary>
        public async Task<string> GetCompletionAsync(CompletionRequest req, CancellationToken ct)
        {
            if (req == null) return null;
            if (string.IsNullOrWhiteSpace(req.ServerUrl)) return null;
            if (string.IsNullOrWhiteSpace(req.Model)) return null;

            string url = req.ServerUrl.TrimEnd('/') + "/api/generate";

            var payload = new JObject
            {
                ["model"] = req.Model,
                ["prompt"] = req.Prefix ?? string.Empty,
                ["suffix"] = req.Suffix ?? string.Empty,
                ["stream"] = false,
                ["options"] = new JObject
                {
                    ["temperature"] = 0.2,
                    ["top_p"] = 0.95,
                    ["num_predict"] = req.MaxPredict,
                    // Common FIM/EOT stop tokens across code models. Model templates
                    // usually handle these, but we belt-and-suspenders here.
                    ["stop"] = new JArray
                    {
                        "<|endoftext|>", "<|fim_pad|>", "<|file_sep|>", "<EOT>",
                        "<|im_end|>", "<|im_start|>"
                    }
                }
            };

            // Per-request linked timeout so a hung server doesn't pin a suggestion forever.
            using (var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(1, req.TimeoutSeconds))))
            using (var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token))
            using (var http = new HttpRequestMessage(HttpMethod.Post, url))
            {
                http.Content = new StringContent(payload.ToString(Formatting.None), Encoding.UTF8, "application/json");

                if (req.UseAuth)
                {
                    string raw = (req.Username ?? string.Empty) + ":" + (req.Password ?? string.Empty);
                    string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
                    http.Headers.Authorization = new AuthenticationHeaderValue("Basic", b64);
                }

                Logger.Log("Http", $"POST {url} model={req.Model} predict={req.MaxPredict} timeout={req.TimeoutSeconds}s");
                var sw = Stopwatch.StartNew();
                try
                {
                    using (var resp = await s_http.SendAsync(http, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false))
                    {
                        sw.Stop();
                        Logger.Log("Http", $"{(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms");

                        if (!resp.IsSuccessStatusCode) return null;

                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var parsed = JObject.Parse(body);
                        string completion = (string)parsed["response"];
                        if (completion == null)
                        {
                            Logger.Log("Http", "response field missing or non-string");
                            completion = string.Empty;
                        }
                        return PostProcess(completion);
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // User kept typing — propagate so the session can stay quiet.
                    throw;
                }
                catch (OperationCanceledException)
                {
                    // Linked token cancelled by timeoutCts — request exceeded TimeoutSeconds.
                    Logger.Log("Http", $"timeout after {req.TimeoutSeconds}s");
                    return null;
                }
                catch (HttpRequestException ex)
                {
                    Logger.LogException("Http", ex);
                    return null;
                }
                catch (JsonReaderException ex)
                {
                    Logger.LogException("Http", ex);
                    return null;
                }
            }
        }

        // ---- Server discovery + diagnostics (used by the Options page UI) ----

        /// <summary>
        /// Returns the alphabetically-sorted list of model names available on the
        /// configured Ollama server (GET /api/tags). Throws on any failure — the
        /// caller is expected to surface the message to the user.
        /// </summary>
        public static async Task<List<string>> ListModelsAsync(
            string serverUrl, bool useAuth, string username, string password, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                throw new ArgumentException("Server URL is required.", nameof(serverUrl));

            string url = serverUrl.TrimEnd('/') + "/api/tags";

            using (var http = new HttpRequestMessage(HttpMethod.Get, url))
            {
                ApplyBasicAuth(http, useAuth, username, password);
                Logger.Log("Http", $"GET {url}");
                var sw = Stopwatch.StartNew();
                using (var resp = await s_http.SendAsync(http, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                {
                    sw.Stop();
                    Logger.Log("Http", $"{(int)resp.StatusCode} in {sw.ElapsedMilliseconds}ms");

                    if (!resp.IsSuccessStatusCode)
                        throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    var parsed = JObject.Parse(body);
                    var models = parsed["models"] as JArray;
                    var result = new List<string>();
                    if (models != null)
                    {
                        foreach (var m in models)
                        {
                            string name = (string)m["name"];
                            if (!string.IsNullOrEmpty(name)) result.Add(name);
                        }
                    }
                    result.Sort(StringComparer.OrdinalIgnoreCase);
                    return result;
                }
            }
        }

        /// <summary>Result of <see cref="TestConnectionAsync"/>.</summary>
        public sealed class TestConnectionResult
        {
            public bool Success { get; }
            public string Message { get; }
            public TestConnectionResult(bool success, string message)
            {
                Success = success;
                Message = message ?? string.Empty;
            }
        }

        /// <summary>
        /// GETs /api/tags to verify reachability + auth, then checks the configured
        /// model is on the server. Expected failures (HTTP, JSON, auth) come back as
        /// <see cref="TestConnectionResult"/> with <see cref="TestConnectionResult.Success"/>=false;
        /// truly unexpected exceptions propagate to the caller.
        /// </summary>
        public static async Task<TestConnectionResult> TestConnectionAsync(
            string serverUrl, string model, bool useAuth, string username, string password, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(serverUrl))
                return new TestConnectionResult(false, "Server URL is empty.");

            string url = serverUrl.TrimEnd('/') + "/api/tags";

            using (var http = new HttpRequestMessage(HttpMethod.Get, url))
            {
                ApplyBasicAuth(http, useAuth, username, password);
                Logger.Log("Http", $"GET {url} (test)");
                try
                {
                    using (var resp = await s_http.SendAsync(http, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false))
                    {
                        Logger.Log("Http", $"{(int)resp.StatusCode} (test)");
                        if (resp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                            return new TestConnectionResult(false, "Authentication failed (HTTP 401).");
                        if (!resp.IsSuccessStatusCode)
                            return new TestConnectionResult(false, $"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}");

                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var parsed = JObject.Parse(body);
                        var models = parsed["models"] as JArray;
                        if (models == null)
                            return new TestConnectionResult(false, "Unexpected response shape (no 'models' field).");

                        if (string.IsNullOrWhiteSpace(model))
                            return new TestConnectionResult(true, $"{models.Count} model(s) available.");

                        foreach (var m in models)
                        {
                            string name = (string)m["name"];
                            if (string.Equals(name, model, StringComparison.OrdinalIgnoreCase))
                                return new TestConnectionResult(true, $"Model '{model}' is available.");
                        }
                        return new TestConnectionResult(false, $"Connected, but model '{model}' is not on the server.");
                    }
                }
                catch (HttpRequestException ex)
                {
                    Logger.LogException("Http", ex);
                    return new TestConnectionResult(false, ex.Message);
                }
                catch (JsonReaderException ex)
                {
                    Logger.LogException("Http", ex);
                    return new TestConnectionResult(false, $"Bad response: {ex.Message}");
                }
            }
        }

        /// <summary>Adds an <c>Authorization: Basic …</c> header when auth is enabled.</summary>
        private static void ApplyBasicAuth(HttpRequestMessage http, bool useAuth, string username, string password)
        {
            if (!useAuth) return;
            string raw = (username ?? string.Empty) + ":" + (password ?? string.Empty);
            string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(raw));
            http.Headers.Authorization = new AuthenticationHeaderValue("Basic", b64);
        }

        /// <summary>Strip stray FIM/EOT sentinels that occasionally leak through.</summary>
        private static string PostProcess(string s)
        {
            if (string.IsNullOrEmpty(s)) return s;
            string[] junk =
            {
                "<|endoftext|>", "<|fim_pad|>", "<|file_sep|>", "<EOT>",
                "<|fim_middle|>", "<|fim_prefix|>", "<|fim_suffix|>",
                "<|im_end|>", "<|im_start|>",
            };
            foreach (var j in junk)
            {
                int idx = s.IndexOf(j, StringComparison.Ordinal);
                if (idx >= 0) s = s.Substring(0, idx);
            }
            return s;
        }
    }
}
