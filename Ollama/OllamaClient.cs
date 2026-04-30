using System;
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
