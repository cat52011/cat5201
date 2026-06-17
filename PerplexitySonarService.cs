using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    /// <summary>
    /// Perplexity Sonar API 封裝（主聊天層）
    /// 支援：
    /// 1. 一次性完整回傳
    /// 2. SSE 串流逐步輸出
    ///
    /// API Key 讀取：環境變數 PERPLEXITY_API_KEY
    /// Endpoint：POST https://api.perplexity.ai/v1/chat/completions
    /// </summary>
    public sealed class PerplexitySonarService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly string _apiKey;
        private readonly string _model;

        public PerplexitySonarService(string model = "sonar")
        {
            _model = NormalizeModel(model);
            _apiKey = Environment.GetEnvironmentVariable("PERPLEXITY_API_KEY") ?? "";

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "找不到 PERPLEXITY_API_KEY。請先在系統環境變數設定 PERPLEXITY_API_KEY。");
            }
        }

        private static string NormalizeModel(string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return "sonar";

            var m = model.Trim();

            if (string.Equals(m, "sonar", StringComparison.OrdinalIgnoreCase))
                return "sonar";

            if (string.Equals(m, "sonar-deep-research", StringComparison.OrdinalIgnoreCase))
                return "sonar-deep-research";

            return "sonar";
        }

        public Task<string> GenerateAsync(
            string instructions,
            string userText,
            int maxOutputTokens = 8000,
            CancellationToken ct = default,
            Action<int, int>? onUsage = null)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return Task.FromResult("");

            return GenerateInternalAsync(
                instructions,
                userText,
                stream: false,
                onDelta: null,
                maxOutputTokens: maxOutputTokens,
                ct: ct,
                onUsage: onUsage);
        }

        public Task<string> GenerateStreamAsync(
            string instructions,
            string userText,
            Action<string>? onDelta,
            int maxOutputTokens = 8000,
            CancellationToken ct = default,
            Action<int, int>? onUsage = null)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return Task.FromResult("");

            return GenerateInternalAsync(
                instructions,
                userText,
                stream: true,
                onDelta: onDelta,
                maxOutputTokens: maxOutputTokens,
                ct: ct,
                onUsage: onUsage);
        }

        private async Task<string> GenerateInternalAsync(
            string instructions,
            string userText,
            bool stream,
            Action<string>? onDelta,
            int maxOutputTokens,
            CancellationToken ct,
            Action<int, int>? onUsage = null)
        {
            var payload = new
            {
                model = _model,
                messages = new object[]
                {
                    new
                    {
                        role = "system",
                        content = instructions ?? ""
                    },
                    new
                    {
                        role = "user",
                        content = userText ?? ""
                    }
                },
                stream = stream,
                max_tokens = maxOutputTokens
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.perplexity.ai/v1/sonar");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            if (stream)
            {
                req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
            }

            req.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
            {
                var err = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException($"Perplexity Sonar API 失敗 ({(int)resp.StatusCode}): {err}");
            }

            if (!stream)
            {
                var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                TryExtractUsage(body, onUsage);
                return ExtractTextFromJson(body);
            }

            using var responseStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(responseStream, Encoding.UTF8);

            string? currentEventName = null;
            var dataBuilder = new StringBuilder();
            var finalText = new StringBuilder();
            var usage = new Usage();

            while (!reader.EndOfStream)
            {
                ct.ThrowIfCancellationRequested();

                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line == null)
                    break;

                if (line.Length == 0)
                {
                    if (dataBuilder.Length > 0)
                    {
                        var data = dataBuilder.ToString().Trim();

                        if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                            break;

                        ProcessSseEvent(currentEventName, data, onDelta, finalText, usage);
                    }

                    currentEventName = null;
                    dataBuilder.Clear();
                    continue;
                }

                if (line.StartsWith("event:", StringComparison.OrdinalIgnoreCase))
                {
                    currentEventName = line.Substring("event:".Length).Trim();
                    continue;
                }

                if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                {
                    if (dataBuilder.Length > 0)
                        dataBuilder.Append('\n');

                    dataBuilder.Append(line.Substring("data:".Length).TrimStart());
                }
            }

            if (onUsage != null && (usage.Input > 0 || usage.Output > 0))
                onUsage(usage.Input, usage.Output);

            return finalText.ToString().Trim();
        }

        private sealed class Usage
        {
            public int Input;
            public int Output;
        }

        private static void ProcessSseEvent(
            string? eventName,
            string data,
            Action<string>? onDelta,
            StringBuilder finalText,
            Usage usage)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                // Perplexity 在串流的最後一個 chunk 帶 usage（OpenAI 相容格式）。
                if (root.TryGetProperty("usage", out var usageEl) && usageEl.ValueKind == JsonValueKind.Object)
                {
                    if (usageEl.TryGetProperty("prompt_tokens", out var pEl) && pEl.TryGetInt32(out var pv))
                        usage.Input = pv;
                    if (usageEl.TryGetProperty("completion_tokens", out var cEl) && cEl.TryGetInt32(out var cv))
                        usage.Output = cv;
                }

                if (root.TryGetProperty("choices", out var choicesEl) &&
                    choicesEl.ValueKind == JsonValueKind.Array &&
                    choicesEl.GetArrayLength() > 0)
                {
                    var first = choicesEl[0];

                    if (first.TryGetProperty("delta", out var deltaEl) &&
                        deltaEl.ValueKind == JsonValueKind.Object &&
                        deltaEl.TryGetProperty("content", out var contentEl) &&
                        contentEl.ValueKind == JsonValueKind.String)
                    {
                        var delta = contentEl.GetString() ?? "";
                        if (!string.IsNullOrEmpty(delta))
                        {
                            finalText.Append(delta);
                            onDelta?.Invoke(delta);
                        }
                    }
                }
            }
            catch
            {
                // 不因單一事件解析失敗而中止整條串流
            }
        }

        private static void TryExtractUsage(string json, Action<int, int>? onUsage)
        {
            if (onUsage == null)
                return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("usage", out var usageEl))
                    return;

                int input = usageEl.TryGetProperty("prompt_tokens", out var pEl) && pEl.TryGetInt32(out var pv) ? pv : 0;
                int output = usageEl.TryGetProperty("completion_tokens", out var cEl) && cEl.TryGetInt32(out var cv) ? cv : 0;

                if (input > 0 || output > 0)
                    onUsage(input, output);
            }
            catch
            {
                // usage 解析失敗不影響主回應。
            }
        }

        private static string ExtractTextFromJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("choices", out var choicesEl) ||
                    choicesEl.ValueKind != JsonValueKind.Array ||
                    choicesEl.GetArrayLength() == 0)
                    return "";

                var first = choicesEl[0];

                if (!first.TryGetProperty("message", out var msgEl) ||
                    msgEl.ValueKind != JsonValueKind.Object)
                    return "";

                if (!msgEl.TryGetProperty("content", out var contentEl))
                    return "";

                return (contentEl.GetString() ?? "").Trim();
            }
            catch
            {
                return "";
            }
        }
    }
}