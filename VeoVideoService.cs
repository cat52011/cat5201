using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    /// <summary>
    /// Video Gen v1：Google Veo 3 影片生成（非同步、長時間操作 long-running operation）。主要影片產生器。
    ///
    /// 休眠預設：需 GEMINI_API_KEY（或 GOOGLE_API_KEY）且環境變數 CAT5201_VEO_ENABLED=1 才會呼叫。
    /// 未啟用時影片任務只走 Claude 計畫（劇本/分鏡/旁白/鏡頭），不呼叫 API、不影響主答案。
    ///
    /// 端點（已對 ai.google.dev/gemini-api/docs/video 校正，2026-06）：
    ///   建立：POST .../models/{model}:predictLongRunning
    ///         body {instances:[{prompt}], parameters:{aspectRatio,resolution,durationSeconds,numberOfVideos}}
    ///         header x-goog-api-key → 回 {name: operationName}
    ///   輪詢：GET  .../{operationName}（header x-goog-api-key）→ {done, response:{...}, error:{...}}
    ///   取得影片：response.generateVideoResponse.generatedSamples[0].video.uri（用 x-goog-api-key header 下載）。
    /// 完成判定：done==true。
    /// </summary>
    public sealed class VeoVideoService
    {
        private static readonly HttpClient _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };

        private const string ApiBase = "https://generativelanguage.googleapis.com/v1beta";

        private readonly string _apiKey;
        private readonly string _model;

        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(8);
        private const int MaxPolls = 90; // ~12 分鐘

        public VeoVideoService()
        {
            _apiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY")
                      ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY")
                      ?? "";

            string modelEnv = Environment.GetEnvironmentVariable("CAT5201_VEO_MODEL") ?? "";
            // Veo 3 GA model id（其他可用：veo-3.1-generate-preview / veo-3.0-fast-generate-001）。
            _model = string.IsNullOrWhiteSpace(modelEnv) ? "veo-3.0-generate-001" : modelEnv;
        }

        // 安全閘門已移除：只要有 GEMINI_API_KEY / GOOGLE_API_KEY 就會實際呼叫 Veo（會計費）。
        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        public string Model => _model;

        public string NotConfiguredReason =>
            string.IsNullOrWhiteSpace(_apiKey)
                ? "找不到 GEMINI_API_KEY / GOOGLE_API_KEY。"
                : "Veo 3 未啟用。";

        public sealed class VeoResult
        {
            public bool Success { get; init; }
            public byte[] Mp4Bytes { get; init; } = Array.Empty<byte>();
            public string OperationName { get; init; } = "";
            public VideoGenerationStatus Status { get; init; } = VideoGenerationStatus.Failed;
            public string ErrorMessage { get; init; } = "";
        }

        public async Task<VeoResult> GenerateAsync(
            string prompt,
            int seconds,
            string aspectRatio,
            Action<int, VideoGenerationStatus>? onProgress,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return Fail("影片描述為空。");

            onProgress?.Invoke(0, VideoGenerationStatus.Queued);

            string operationName;
            try
            {
                operationName = await CreateOperationAsync(prompt, seconds, aspectRatio, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Fail($"建立 Veo 影片任務失敗：{ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(operationName))
                return Fail("Veo API 未回傳 operation name。");

            for (int i = 0; i < MaxPolls; i++)
            {
                try { await Task.Delay(PollInterval, ct); }
                catch (OperationCanceledException) { throw; }

                JsonDocument doc;
                try
                {
                    doc = await PollAsync(operationName, ct);
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    return Fail($"查詢 Veo 影片狀態失敗：{ex.Message}", operationName);
                }

                using (doc)
                {
                    var root = doc.RootElement;
                    bool done = root.TryGetProperty("done", out var doneEl) &&
                                doneEl.ValueKind == JsonValueKind.True;

                    onProgress?.Invoke(done ? 100 : Math.Min(90, 10 + i * 5),
                        done ? VideoGenerationStatus.Completed : VideoGenerationStatus.Generating);

                    if (!done)
                        continue;

                    if (root.TryGetProperty("error", out var errEl))
                    {
                        string msg = errEl.TryGetProperty("message", out var m) ? (m.GetString() ?? "") : "Veo 生成失敗。";
                        return Fail(msg, operationName);
                    }

                    try
                    {
                        byte[] bytes = await ExtractVideoBytesAsync(root, ct);
                        if (bytes.Length == 0)
                            return Fail("Veo 回應沒有可用的影片內容。", operationName);

                        return new VeoResult
                        {
                            Success = true,
                            Mp4Bytes = bytes,
                            OperationName = operationName,
                            Status = VideoGenerationStatus.Completed
                        };
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return Fail($"取得 Veo 影片內容失敗：{ex.Message}", operationName);
                    }
                }
            }

            return Fail("Veo 影片生成逾時（超過輪詢上限）。", operationName);
        }

        private async Task<string> CreateOperationAsync(string prompt, int seconds, string aspectRatio, CancellationToken ct)
        {
            string url = $"{ApiBase}/models/{_model}:predictLongRunning";

            // 注意：veo-3.0-generate-001 不接受 numberOfVideos（會回 400 INVALID_ARGUMENT），故不送此欄位。
            var payload = new
            {
                instances = new[] { new { prompt } },
                parameters = new
                {
                    aspectRatio = string.IsNullOrWhiteSpace(aspectRatio) ? "9:16" : aspectRatio,
                    resolution = "720p",
                    // durationSeconds 必須是「數字」，不能是字串（送字串會回 400 INVALID_ARGUMENT）。
                    durationSeconds = seconds > 0 ? seconds : 8
                }
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
            };
            req.Headers.Add("x-goog-api-key", _apiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException(DescribeVeoError((int)resp.StatusCode, body));

            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.TryGetProperty("name", out var n) ? (n.GetString() ?? "") : "";
        }

        // 把 Veo 的 HTTP 錯誤翻成「使用者看得懂、知道下一步」的訊息。
        // 重點：429/403 不是程式 bug，而是帳號層級（Veo 是付費模型，免費額度不含影片生成）。
        private static string DescribeVeoError(int status, string body)
        {
            switch (status)
            {
                case 429:
                    return "Veo 3 是付費模型，目前的 Gemini 額度無法生成影片（429 配額用盡）。" +
                           "免費方案不含 Veo 影片生成——需在 Google Cloud 專案啟用帳單後，Veo 才會真正出片。" +
                           "（這代表請求格式已正確、只差付費權限；Claude 影片計畫已完整保留。）";
                case 403:
                    return "這支 Gemini 金鑰沒有 Veo 影片生成權限（403）——需啟用帳單 / 取得 Veo 存取權。";
                case 400:
                    return $"Veo 請求被拒（400 參數錯誤）：{Truncate(body, 300)}";
                default:
                    return $"Veo API {status}：{Truncate(body, 300)}";
            }
        }

        private async Task<JsonDocument> PollAsync(string operationName, CancellationToken ct)
        {
            string url = $"{ApiBase}/{operationName}";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Add("x-goog-api-key", _apiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"Veo API {(int)resp.StatusCode}：{Truncate(body, 300)}");

            return JsonDocument.Parse(body);
        }

        // Veo 回應可能是 base64 影片，或一個可下載 uri；兩者都嘗試。
        private async Task<byte[]> ExtractVideoBytesAsync(JsonElement operationRoot, CancellationToken ct)
        {
            if (!operationRoot.TryGetProperty("response", out var response))
                return Array.Empty<byte>();

            string b64 = FindFirstString(response, "bytesBase64Encoded", "videoBytes", "b64_json");
            if (!string.IsNullOrWhiteSpace(b64))
                return Convert.FromBase64String(b64);

            // 文件路徑：response.generateVideoResponse.generatedSamples[0].video.uri（用 x-goog-api-key header 下載）。
            string uri = FindFirstString(response, "uri", "videoUri", "url");
            if (!string.IsNullOrWhiteSpace(uri))
            {
                using var dlReq = new HttpRequestMessage(HttpMethod.Get, uri);
                dlReq.Headers.Add("x-goog-api-key", _apiKey);

                using var dlResp = await _http.SendAsync(dlReq, HttpCompletionOption.ResponseHeadersRead, ct);
                if (dlResp.IsSuccessStatusCode)
                    return await dlResp.Content.ReadAsByteArrayAsync(ct);
            }

            return Array.Empty<byte>();
        }

        // 在巢狀 JSON 中遞迴找第一個指定名稱的字串值。
        private static string FindFirstString(JsonElement el, params string[] names)
        {
            switch (el.ValueKind)
            {
                case JsonValueKind.Object:
                    foreach (var prop in el.EnumerateObject())
                    {
                        foreach (var name in names)
                        {
                            if (string.Equals(prop.Name, name, StringComparison.OrdinalIgnoreCase) &&
                                prop.Value.ValueKind == JsonValueKind.String)
                            {
                                return prop.Value.GetString() ?? "";
                            }
                        }

                        string nested = FindFirstString(prop.Value, names);
                        if (!string.IsNullOrWhiteSpace(nested))
                            return nested;
                    }
                    break;

                case JsonValueKind.Array:
                    foreach (var item in el.EnumerateArray())
                    {
                        string nested = FindFirstString(item, names);
                        if (!string.IsNullOrWhiteSpace(nested))
                            return nested;
                    }
                    break;
            }

            return "";
        }

        private static VeoResult Fail(string error, string op = "")
            => new VeoResult { Success = false, Status = VideoGenerationStatus.Failed, ErrorMessage = error, OperationName = op };

        private static string Truncate(string s, int max)
        {
            s ??= "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
