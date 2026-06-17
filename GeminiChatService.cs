using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    /// <summary>
    /// 最小可用的 Gemini generateContent API 封裝（v1：非串流，文字輸入/輸出）。
    /// API Key 讀取：環境變數 GEMINI_API_KEY（或 GOOGLE_API_KEY）。
    /// 端點：POST https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent（header x-goog-api-key）。
    /// </summary>
    public sealed class GeminiChatService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta";

        private readonly string _apiKey;
        private readonly string _model;

        public GeminiChatService(string model = "gemini-2.5-pro")
        {
            _model = string.IsNullOrWhiteSpace(model) ? "gemini-2.5-pro" : model;
            _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                      ?? "";

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "找不到 GEMINI_API_KEY / GOOGLE_API_KEY。請先在系統環境變數設定。");
            }
        }

        public async Task<string> GenerateAsync(
            string instructions,
            string userText,
            int maxOutputTokens = 8000,
            CancellationToken ct = default,
            Action<int, int>? onUsage = null)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return "";

            var generationConfig = new { maxOutputTokens = maxOutputTokens > 0 ? maxOutputTokens : 8000 };

            object payload = string.IsNullOrWhiteSpace(instructions)
                ? new
                {
                    contents = new[] { new { role = "user", parts = new[] { new { text = userText } } } },
                    generationConfig
                }
                : new
                {
                    contents = new[] { new { role = "user", parts = new[] { new { text = userText } } } },
                    systemInstruction = new { parts = new[] { new { text = instructions } } },
                    generationConfig
                };

            string url = $"{ApiBase}/models/{_model}:generateContent";

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-goog-api-key", _apiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            string body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Gemini API {(int)resp.StatusCode}：{Truncate(body, 400)}");

            TryExtractUsage(body, onUsage);
            return ExtractText(body);
        }

        // usageMetadata.{promptTokenCount, candidatesTokenCount}
        private static void TryExtractUsage(string json, Action<int, int>? onUsage)
        {
            if (onUsage == null)
                return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("usageMetadata", out var usageEl))
                    return;

                int input = usageEl.TryGetProperty("promptTokenCount", out var pEl) && pEl.TryGetInt32(out var pv) ? pv : 0;
                int output = usageEl.TryGetProperty("candidatesTokenCount", out var cEl) && cEl.TryGetInt32(out var cv) ? cv : 0;

                if (input > 0 || output > 0)
                    onUsage(input, output);
            }
            catch
            {
                // usage 解析失敗不影響主回應。
            }
        }

        private static string ExtractText(string body)
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("candidates", out var cands) ||
                cands.ValueKind != JsonValueKind.Array ||
                cands.GetArrayLength() == 0)
            {
                if (root.TryGetProperty("promptFeedback", out var pf) &&
                    pf.TryGetProperty("blockReason", out var br))
                {
                    throw new InvalidOperationException($"Gemini 拒絕回應（{br.GetString()}）。");
                }
                return "";
            }

            var sb = new StringBuilder();
            var first = cands[0];
            if (first.TryGetProperty("content", out var content) &&
                content.TryGetProperty("parts", out var parts) &&
                parts.ValueKind == JsonValueKind.Array)
            {
                foreach (var p in parts.EnumerateArray())
                {
                    if (p.TryGetProperty("text", out var t) && t.ValueKind == JsonValueKind.String)
                        sb.Append(t.GetString());
                }
            }

            return sb.ToString();
        }

        private static string Truncate(string s, int max)
        {
            s ??= "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
