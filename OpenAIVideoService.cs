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
    /// Video Gen v1：OpenAI 影片 API（Sora）封裝 —— 非同步、長時間、需輪詢。
    ///
    /// 休眠預設：需同時具備 OPENAI_API_KEY 與環境變數 CAT5201_VIDEO_ENABLED=1 才會真正呼叫，
    /// 避免在沒有 Sora 影片權限 / 想控制花費時誤觸發。未啟用時行為與現在相同（generate_video 階段標記未啟用，不影響主答案）。
    ///
    /// 端點假設（需在有 Sora 權限時做端對端驗證；調整點集中在此檔）：
    ///   建立：POST /v1/videos                 body {model, prompt, seconds, size} → {id, status}
    ///   輪詢：GET  /v1/videos/{id}             → {status, progress}
    ///   下載：GET  /v1/videos/{id}/content     → mp4 bytes
    ///   取消：POST /v1/videos/{id}/cancel      （best effort）
    /// 完成值接受 completed/complete/succeeded/success；失敗值 failed/error。
    /// </summary>
    public sealed class OpenAIVideoService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            Timeout = Timeout.InfiniteTimeSpan // 由 CancellationToken / 輪詢上限控制
        };

        private readonly string _apiKey;
        private readonly string _model;
        private readonly bool _enabledFlag;

        // 輪詢設定：每 5 秒一次，最多 ~10 分鐘。
        private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
        private const int MaxPolls = 120;

        public OpenAIVideoService()
        {
            _apiKey = ApiKeyStore.Resolve("OPENAI_API_KEY");

            string modelEnv = Environment.GetEnvironmentVariable("CAT5201_VIDEO_MODEL") ?? "";
            _model = string.IsNullOrWhiteSpace(modelEnv) ? "sora-2" : modelEnv;

            string flag = Environment.GetEnvironmentVariable("CAT5201_VIDEO_ENABLED") ?? "";
            _enabledFlag =
                string.Equals(flag, "1", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(flag, "true", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(flag, "yes", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>是否已啟用（有金鑰且明確開啟旗標）。未啟用時呼叫端應跳過實際生成。</summary>
        public bool IsConfigured => _enabledFlag && !string.IsNullOrWhiteSpace(_apiKey);

        public string NotConfiguredReason
        {
            get
            {
                if (string.IsNullOrWhiteSpace(_apiKey))
                    return "找不到 OPENAI_API_KEY。";
                if (!_enabledFlag)
                    return "影片生成尚未啟用（v1 scaffolding）。設定環境變數 CAT5201_VIDEO_ENABLED=1，並確認帳號具備 Sora 影片 API 權限後即可使用。";
                return "影片生成未啟用。";
            }
        }

        public string Model => _model;

        public sealed class VideoResult
        {
            public bool Success { get; init; }
            public byte[] Mp4Bytes { get; init; } = Array.Empty<byte>();
            public string JobId { get; init; } = "";
            public VideoGenerationStatus Status { get; init; } = VideoGenerationStatus.Failed;
            public string ErrorMessage { get; init; } = "";
        }

        /// <summary>
        /// 高階入口：建立影片任務 → 輪詢直到完成 / 失敗 → 下載 mp4。
        /// onProgress(percent, statusLabel) 用於長時間進度 UI。支援取消（best-effort 取消遠端任務）。
        /// </summary>
        public async Task<VideoResult> GenerateAsync(
            string prompt,
            int seconds,
            string size,
            Action<int, VideoGenerationStatus>? onProgress,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(prompt))
                return Fail("影片描述為空。");

            onProgress?.Invoke(0, VideoGenerationStatus.Queued);

            string jobId;
            try
            {
                (jobId, _) = await CreateAsync(prompt, seconds, size, ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return Fail($"建立影片任務失敗：{ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(jobId))
                return Fail("影片 API 未回傳 job id。");

            for (int i = 0; i < MaxPolls; i++)
            {
                try
                {
                    await Task.Delay(PollInterval, ct);
                }
                catch (OperationCanceledException)
                {
                    await TryCancelAsync(jobId);
                    throw;
                }

                VideoGenerationStatus status;
                int progress;
                string error;

                try
                {
                    (status, progress, error) = await PollAsync(jobId, ct);
                }
                catch (OperationCanceledException)
                {
                    await TryCancelAsync(jobId);
                    throw;
                }
                catch (Exception ex)
                {
                    return Fail($"查詢影片狀態失敗：{ex.Message}", jobId);
                }

                onProgress?.Invoke(progress, status);

                if (status == VideoGenerationStatus.Completed)
                {
                    try
                    {
                        byte[] bytes = await DownloadContentAsync(jobId, ct);
                        if (bytes.Length == 0)
                            return Fail("影片下載為空。", jobId);

                        return new VideoResult
                        {
                            Success = true,
                            Mp4Bytes = bytes,
                            JobId = jobId,
                            Status = VideoGenerationStatus.Completed
                        };
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        return Fail($"下載影片失敗：{ex.Message}", jobId);
                    }
                }

                if (status == VideoGenerationStatus.Failed)
                    return Fail(string.IsNullOrWhiteSpace(error) ? "影片生成失敗。" : error, jobId);
            }

            await TryCancelAsync(jobId);
            return Fail("影片生成逾時（超過輪詢上限）。", jobId);
        }

        private async Task<(string JobId, string Status)> CreateAsync(
            string prompt, int seconds, string size, CancellationToken ct)
        {
            var payload = new
            {
                model = _model,
                prompt = prompt,
                seconds = seconds > 0 ? seconds.ToString() : "4",
                size = string.IsNullOrWhiteSpace(size) ? "720x1280" : size
            };

            using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/videos");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
            req.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI Video API {(int)resp.StatusCode}：{Truncate(body, 400)}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string id = root.TryGetProperty("id", out var idEl) ? (idEl.GetString() ?? "") : "";
            string status = root.TryGetProperty("status", out var stEl) ? (stEl.GetString() ?? "") : "";
            return (id, status);
        }

        private async Task<(VideoGenerationStatus Status, int Progress, string Error)> PollAsync(
            string jobId, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.openai.com/v1/videos/{jobId}");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            string body = await resp.Content.ReadAsStringAsync(ct);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenAI Video API {(int)resp.StatusCode}：{Truncate(body, 300)}");

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            string statusRaw = root.TryGetProperty("status", out var stEl) ? (stEl.GetString() ?? "") : "";

            int progress = 0;
            if (root.TryGetProperty("progress", out var pEl))
            {
                if (pEl.ValueKind == JsonValueKind.Number)
                    progress = (int)Math.Round(pEl.GetDouble());
                else if (pEl.ValueKind == JsonValueKind.String && int.TryParse(pEl.GetString(), out var pi))
                    progress = pi;
            }

            string error = "";
            if (root.TryGetProperty("error", out var errEl))
            {
                error = errEl.ValueKind == JsonValueKind.String
                    ? (errEl.GetString() ?? "")
                    : (errEl.TryGetProperty("message", out var msgEl) ? (msgEl.GetString() ?? "") : "");
            }

            return (MapStatus(statusRaw), progress, error);
        }

        private async Task<byte[]> DownloadContentAsync(string jobId, CancellationToken ct)
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, $"https://api.openai.com/v1/videos/{jobId}/content");
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!resp.IsSuccessStatusCode)
            {
                string body = await resp.Content.ReadAsStringAsync(ct);
                throw new InvalidOperationException($"下載影片 HTTP {(int)resp.StatusCode}：{Truncate(body, 300)}");
            }

            return await resp.Content.ReadAsByteArrayAsync(ct);
        }

        // best effort：取消時嘗試通知遠端，不拋例外。
        private async Task TryCancelAsync(string jobId)
        {
            if (string.IsNullOrWhiteSpace(jobId))
                return;

            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"https://api.openai.com/v1/videos/{jobId}/cancel");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                using var _ = await _http.SendAsync(req, CancellationToken.None);
            }
            catch
            {
                // 取消通知失敗無妨。
            }
        }

        private static VideoGenerationStatus MapStatus(string raw)
        {
            string s = (raw ?? "").Trim().ToLowerInvariant();
            return s switch
            {
                "completed" or "complete" or "succeeded" or "success" => VideoGenerationStatus.Completed,
                "failed" or "error" => VideoGenerationStatus.Failed,
                "queued" or "pending" or "created" => VideoGenerationStatus.Queued,
                _ => VideoGenerationStatus.Generating // in_progress / processing / running…
            };
        }

        private static VideoResult Fail(string error, string jobId = "")
            => new VideoResult { Success = false, Status = VideoGenerationStatus.Failed, ErrorMessage = error, JobId = jobId };

        private static string Truncate(string s, int max)
        {
            s ??= "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
