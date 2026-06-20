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

        public OpenAIChatService(string model = "gpt-5.5")
        {
            _model = model;
            _apiKey = ApiKeyStore.Resolve("OPENAI_API_KEY");

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
            CancellationToken ct = default,
            Action<int, int>? onUsage = null)
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

            return GenerateAsync(instructions, input, maxOutputTokens, ct, onUsage);
        }

        // GPT-5.x 是 reasoning model，Responses API 內部會夾 temperature，gpt-5.x 拒絕。
        // 加 reasoning_effort 可讓 API 切換到 reasoning 路徑並略過 temperature 注入。
        private bool IsReasoningModel =>
            _model.StartsWith("gpt-5", StringComparison.OrdinalIgnoreCase) ||
            _model.StartsWith("o1", StringComparison.OrdinalIgnoreCase) ||
            _model.StartsWith("o3", StringComparison.OrdinalIgnoreCase) ||
            _model.StartsWith("o4", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 多模態：input 直接給 Responses API 支援的結構（含 input_text / input_image / input_file）
        /// 一次性完整回傳
        /// </summary>
        public async Task<string> GenerateAsync(
            string instructions,
            object input,
            int maxOutputTokens = 8000,
            CancellationToken ct = default,
            Action<int, int>? onUsage = null)
        {
            object payload = IsReasoningModel
                ? (object)new
                {
                    model = _model,
                    instructions = instructions,
                    input = input,
                    max_output_tokens = maxOutputTokens,
                    reasoning = new { effort = "medium" }
                }
                : new
                {
                    model = _model,
                    instructions = instructions,
                    input = input,
                    max_output_tokens = maxOutputTokens
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

            TryExtractUsage(body, onUsage);
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
            CancellationToken ct = default,
            Action<int, int>? onUsage = null)
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

            return GenerateStreamAsync(instructions, input, onDelta, maxOutputTokens, ct, onUsage);
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
            CancellationToken ct = default,
            Action<int, int>? onUsage = null)
        {
            object payload = IsReasoningModel
                ? (object)new
                {
                    model = _model,
                    instructions = instructions,
                    input = input,
                    max_output_tokens = maxOutputTokens,
                    reasoning = new { effort = "medium" },
                    stream = true
                }
                : new
                {
                    model = _model,
                    instructions = instructions,
                    input = input,
                    max_output_tokens = maxOutputTokens,
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
            var usage = new Usage();

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

            return finalText.ToString();
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

                string type = "";
                if (root.TryGetProperty("type", out var typeEl))
                    type = typeEl.GetString() ?? "";

                // 串流結束事件帶總 usage：response.completed → response.usage.{input_tokens,output_tokens}
                if (string.Equals(type, "response.completed", StringComparison.OrdinalIgnoreCase) &&
                    root.TryGetProperty("response", out var respEl) &&
                    respEl.TryGetProperty("usage", out var usageEl))
                {
                    if (usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.TryGetInt32(out var iv))
                        usage.Input = iv;
                    if (usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.TryGetInt32(out var ov))
                        usage.Output = ov;
                }

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

        // 非串流回應的 usage：root.usage.{input_tokens,output_tokens}
        private static void TryExtractUsage(string json, Action<int, int>? onUsage)
        {
            if (onUsage == null)
                return;

            try
            {
                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("usage", out var usageEl))
                    return;

                int input = usageEl.TryGetProperty("input_tokens", out var inEl) && inEl.TryGetInt32(out var iv) ? iv : 0;
                int output = usageEl.TryGetProperty("output_tokens", out var outEl) && outEl.TryGetInt32(out var ov) ? ov : 0;

                if (input > 0 || output > 0)
                    onUsage(input, output);
            }
            catch
            {
                // usage 解析失敗不影響主回應。
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