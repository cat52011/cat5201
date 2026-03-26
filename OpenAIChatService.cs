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
    /// 最小可用的 OpenAI Responses API 呼叫封裝。
    /// 支援：
    /// 1. 一次性完整回傳
    /// 2. SSE 串流逐步輸出
    /// API Key 讀取：環境變數 OPENAI_API_KEY（建議做法）
    /// </summary>
    public sealed class OpenAIChatService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            // 串流模式下不要用固定短 timeout，交給 CancellationToken 控制
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly string _apiKey;
        private readonly string _model;

        public OpenAIChatService(string model = "gpt-5.4")
        {
            _model = model;
            _apiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY") ?? "";

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "找不到 OPENAI_API_KEY。請先在系統環境變數設定 OPENAI_API_KEY，或在啟動前注入到程序環境。");
            }
        }

        /// <summary>
        /// 傳統：純文字 input，一次性完整回傳
        /// </summary>
        public Task<string> GenerateAsync(
            string instructions,
            string userText,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return Task.FromResult("");

            var input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = userText }
                    }
                }
            };

            return GenerateAsync(instructions, input, maxOutputTokens, ct);
        }

        /// <summary>
        /// 多模態：input 直接給 Responses API 支援的結構（含 input_text / input_image / input_file）
        /// 一次性完整回傳
        /// </summary>
        public async Task<string> GenerateAsync(
            string instructions,
            object input,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            var payload = new
            {
                model = _model,
                instructions = instructions,
                input = input,
                max_output_tokens = maxOutputTokens,
                temperature = 0.2
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
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
                throw new InvalidOperationException($"OpenAI API 失敗 ({(int)resp.StatusCode}): {body}");

            return ExtractTextFromResponsesJson(body);
        }

        /// <summary>
        /// 純文字 input 的串流版本。
        /// 每收到文字增量時，會呼叫 onDelta。
        /// 最後回傳完整文字。
        /// </summary>
        public Task<string> GenerateStreamAsync(
            string instructions,
            string userText,
            Action<string>? onDelta,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(userText))
                return Task.FromResult("");

            var input = new object[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = userText }
                    }
                }
            };

            return GenerateStreamAsync(instructions, input, onDelta, maxOutputTokens, ct);
        }

        /// <summary>
        /// 多模態 input 的串流版本。
        /// 每收到文字增量時，會呼叫 onDelta。
        /// 最後回傳完整文字。
        /// </summary>
        public async Task<string> GenerateStreamAsync(
            string instructions,
            object input,
            Action<string>? onDelta,
            int maxOutputTokens = 8000,
            CancellationToken ct = default)
        {
            var payload = new
            {
                model = _model,
                instructions = instructions,
                input = input,
                max_output_tokens = maxOutputTokens,
                temperature = 0.2,
                stream = true
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/responses");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
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
                throw new InvalidOperationException($"OpenAI API 串流失敗 ({(int)resp.StatusCode}): {err}");
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

                // SSE event block 結束
                if (line.Length == 0)
                {
                    if (dataBuilder.Length > 0)
                    {
                        var data = dataBuilder.ToString().Trim();

                        if (string.Equals(data, "[DONE]", StringComparison.Ordinal))
                            break;

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

                // 官方串流事件中，文字增量可由 response.output_text.delta 取得
                // 有些實作會看 eventName，有些直接看 payload.type；這裡兩者都兼容
                bool isTextDelta =
                    string.Equals(type, "response.output_text.delta", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(eventName, "response.output_text.delta", StringComparison.OrdinalIgnoreCase);

                if (isTextDelta)
                {
                    if (root.TryGetProperty("delta", out var deltaEl))
                    {
                        var delta = deltaEl.GetString() ?? "";
                        if (!string.IsNullOrEmpty(delta))
                        {
                            finalText.Append(delta);
                            onDelta?.Invoke(delta);
                        }
                    }
                    return;
                }

                // response.completed / response.in_progress / response.output_item.added ... 皆可忽略
                // response.failed 則視為錯誤
                bool isFailed =
                    string.Equals(type, "response.failed", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(eventName, "response.failed", StringComparison.OrdinalIgnoreCase);

                if (isFailed)
                {
                    string message = "OpenAI 回傳 response.failed。";

                    if (root.TryGetProperty("error", out var errEl))
                    {
                        try
                        {
                            message = errEl.ToString();
                        }
                        catch
                        {
                        }
                    }

                    throw new InvalidOperationException(message);
                }
            }
            catch
            {
                // 不因單一事件解析失敗而中止整條串流
            }
        }

        private static string ExtractTextFromResponsesJson(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);

                if (!doc.RootElement.TryGetProperty("output", out var output) ||
                    output.ValueKind != JsonValueKind.Array)
                    return "";

                var sb = new StringBuilder();

                foreach (var item in output.EnumerateArray())
                {
                    if (!item.TryGetProperty("content", out var content) ||
                        content.ValueKind != JsonValueKind.Array)
                        continue;

                    foreach (var c in content.EnumerateArray())
                    {
                        if (!c.TryGetProperty("type", out var typeEl))
                            continue;

                        var type = typeEl.GetString();
                        if (!string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase))
                            continue;

                        if (c.TryGetProperty("text", out var textEl))
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
                }

                return sb.ToString().Trim();
            }
            catch
            {
                return "";
            }
        }
    }
}