using System;
using System.Collections.Generic;
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
    /// 最小可用的 Claude Messages API 呼叫封裝
    /// 支援：
    /// 1. 一次性完整回傳
    /// 2. SSE 串流逐步輸出
    /// API Key 讀取：環境變數 ANTHROPIC_API_KEY
    /// </summary>
    public sealed class ClaudeChatService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly string _apiKey;
        private readonly string _model;

        public ClaudeChatService(string model = "claude-sonnet-4-6")
        {
            _model = model;
            _apiKey = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY") ?? "";

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "找不到 ANTHROPIC_API_KEY。請先在系統環境變數設定 ANTHROPIC_API_KEY。");
            }
        }

        public Task<string> GenerateAsync(
            string instructions,
            string userText,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return Task.FromResult("");

            var content = new List<object>
            {
                new { type = "text", text = userText }
            };

            return GenerateAsync(instructions, content, maxOutputTokens, ct);
        }

        /// <summary>
        /// contentBlocks 格式：
        /// Claude message content block 陣列
        /// 例如：
        /// new { type = "text", text = "hello" }
        /// new { type = "image", source = ... }
        /// new { type = "document", source = ... }
        /// </summary>
        public async Task<string> GenerateAsync(
            string instructions,
            IEnumerable<object> contentBlocks,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            var payload = new
            {
                model = _model,
                max_tokens = maxOutputTokens,
                system = instructions,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = contentBlocks
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Content = new StringContent(
                JsonSerializer.Serialize(payload),
                Encoding.UTF8,
                "application/json");

            using var resp = await _http.SendAsync(
                req,
                HttpCompletionOption.ResponseHeadersRead,
                ct).ConfigureAwait(false);

            var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Claude API 失敗 ({(int)resp.StatusCode}): {body}");

            return ExtractTextFromClaudeJson(body);
        }

        public Task<string> GenerateStreamAsync(
            string instructions,
            string userText,
            Action<string>? onDelta,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return Task.FromResult("");

            var content = new List<object>
            {
                new { type = "text", text = userText }
            };

            return GenerateStreamAsync(instructions, content, onDelta, maxOutputTokens, ct);
        }

        public async Task<string> GenerateStreamAsync(
            string instructions,
            IEnumerable<object> contentBlocks,
            Action<string>? onDelta,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            var payload = new
            {
                model = _model,
                max_tokens = maxOutputTokens,
                system = instructions,
                stream = true,
                messages = new object[]
                {
                    new
                    {
                        role = "user",
                        content = contentBlocks
                    }
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
            req.Headers.Add("x-api-key", _apiKey);
            req.Headers.Add("anthropic-version", "2023-06-01");
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
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
                throw new InvalidOperationException($"Claude API 串流失敗 ({(int)resp.StatusCode}): {err}");
            }

            using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);

            string? currentEventName = null;
            var dataBuilder = new StringBuilder();
            var finalText = new StringBuilder();

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
                        ProcessSseEvent(currentEventName, data, onDelta, finalText);
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

            return finalText.ToString();
        }

        private static void ProcessSseEvent(
            string? eventName,
            string data,
            Action<string>? onDelta,
            StringBuilder finalText)
        {
            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                string type = "";
                if (root.TryGetProperty("type", out var typeEl))
                    type = typeEl.GetString() ?? "";

                bool isTextDelta =
                    string.Equals(type, "content_block_delta", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(eventName, "content_block_delta", StringComparison.OrdinalIgnoreCase);

                if (isTextDelta &&
                    root.TryGetProperty("delta", out var deltaEl) &&
                    deltaEl.ValueKind == JsonValueKind.Object)
                {
                    var deltaType = deltaEl.TryGetProperty("type", out var dt) ? (dt.GetString() ?? "") : "";
                    if (string.Equals(deltaType, "text_delta", StringComparison.OrdinalIgnoreCase))
                    {
                        var delta = deltaEl.TryGetProperty("text", out var textEl) ? (textEl.GetString() ?? "") : "";
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

        private static string ExtractTextFromClaudeJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                if (!root.TryGetProperty("content", out var content) ||
                    content.ValueKind != JsonValueKind.Array)
                    return "";

                var sb = new StringBuilder();

                foreach (var item in content.EnumerateArray())
                {
                    if (!item.TryGetProperty("type", out var typeEl))
                        continue;

                    var type = typeEl.GetString() ?? "";
                    if (!string.Equals(type, "text", StringComparison.OrdinalIgnoreCase))
                        continue;

                    if (item.TryGetProperty("text", out var textEl))
                    {
                        var t = textEl.GetString();
                        if (!string.IsNullOrWhiteSpace(t))
                        {
                            if (sb.Length > 0)
                                sb.AppendLine();

                            sb.Append(t.Trim());
                        }
                    }
                }

                return sb.ToString().Trim();
            }
            catch
            {
                return "";
            }
        }

        public static object BuildTextBlock(string text)
            => new { type = "text", text = text ?? "" };

        public static object BuildImageBlock(byte[] bytes, string mimeType)
            => new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = mimeType,
                    data = Convert.ToBase64String(bytes)
                }
            };

        public static object BuildPdfBlock(byte[] bytes)
            => new
            {
                type = "document",
                source = new
                {
                    type = "base64",
                    media_type = "application/pdf",
                    data = Convert.ToBase64String(bytes)
                }
            };

        public static object BuildPlainTextDocumentBlock(byte[] bytes, string mimeType)
            => new
            {
                type = "document",
                source = new
                {
                    type = "base64",
                    media_type = mimeType,
                    data = Convert.ToBase64String(bytes)
                }
            };
    }
}