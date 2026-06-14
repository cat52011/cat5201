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
    /// Presentation v2（Gamma + Claude 合作）前置 scaffolding。
    ///
    /// 分工：Claude 產出簡報「內容」（final synthesis），Gamma 負責把它變成漂亮的設計稿。
    /// 流程：POST /generations（inputText + format=presentation + exportAs=pptx）→ 拿 generationId
    ///       → 輪詢 GET /generations/{id} 直到 status=completed/failed → 取 gammaUrl + exportUrl（簽章下載）。
    ///
    /// 目前為「休眠」狀態：沒有 GAMMA_API_KEY 時 IsConfigured=false，呼叫端會直接走既有 PptxBuilder fallback，
    /// 行為與現在完全相同。九月提供 API key（環境變數 GAMMA_API_KEY，格式 sk-gamma-xxx）後即自動啟用。
    ///
    /// 註：回應欄位（status / gammaUrl / exportUrl / credits）依 Gamma API v1.0 文件實作，
    /// 但尚未以真實 key 端對端驗證；九月接上後若欄位有出入，主要調整點在 ParseStatusResponse。
    /// </summary>
    public sealed class GammaPresentationService
    {
        private const string BaseUrl = "https://public-api.gamma.app/v1.0";

        private static readonly HttpClient _http = new HttpClient
        {
            // 生成 + 輪詢可能要 1～數分鐘，交給 CancellationToken / 輪詢上限控制。
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly string _apiKey;

        // 輪詢設定。
        private readonly TimeSpan _pollInterval = TimeSpan.FromSeconds(5);
        private readonly TimeSpan _pollTimeout = TimeSpan.FromMinutes(5);

        public GammaPresentationService()
        {
            _apiKey = Environment.GetEnvironmentVariable("GAMMA_API_KEY") ?? "";
        }

        /// <summary>是否已設定 API key；false 時呼叫端應走 fallback。</summary>
        public bool IsConfigured => !string.IsNullOrWhiteSpace(_apiKey);

        /// <summary>
        /// 用 Gamma 產出簡報並等待完成。失敗或未設定 key 時回傳 Success=false，呼叫端據此 fallback。
        /// </summary>
        public async Task<GammaGenerationResult> GeneratePresentationAsync(
            GammaGenerationInput input,
            CancellationToken ct)
        {
            if (!IsConfigured)
                return GammaGenerationResult.NotConfigured();

            if (input == null || string.IsNullOrWhiteSpace(input.InputText))
                return GammaGenerationResult.Failed("inputText 為空。");

            string generationId;
            try
            {
                generationId = await CreateGenerationAsync(input, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return GammaGenerationResult.Failed($"建立 Gamma 生成失敗：{ex.Message}");
            }

            if (string.IsNullOrWhiteSpace(generationId))
                return GammaGenerationResult.Failed("Gamma 未回傳 generationId。");

            return await PollUntilDoneAsync(generationId, ct).ConfigureAwait(false);
        }

        /// <summary>下載 Gamma 匯出的檔案（exportUrl 為簽章連結，約一週內有效）。</summary>
        public async Task<byte[]> DownloadExportAsync(string exportUrl, CancellationToken ct)
        {
            // exportUrl 通常是簽章 URL，不需附上 API key。
            using var resp = await _http.GetAsync(exportUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            return await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        }

        private async Task<string> CreateGenerationAsync(GammaGenerationInput input, CancellationToken ct)
        {
            // 只放有值的欄位，避免送出 null 造成 400。
            var body = new System.Collections.Generic.Dictionary<string, object?>
            {
                ["inputText"] = input.InputText,
                ["format"] = "presentation",
                // preserve：盡量保留 Claude 已寫好的內容（Claude 負責內容、Gamma 負責設計）。
                ["textMode"] = "preserve",
                ["exportAs"] = string.IsNullOrWhiteSpace(input.ExportAs) ? "pptx" : input.ExportAs
            };

            if (!string.IsNullOrWhiteSpace(input.Title))
                body["title"] = input.Title;

            if (input.NumCards > 0)
                body["numCards"] = input.NumCards;

            if (!string.IsNullOrWhiteSpace(input.AdditionalInstructions))
                body["additionalInstructions"] = input.AdditionalInstructions;

            if (!string.IsNullOrWhiteSpace(input.ThemeId))
                body["themeId"] = input.ThemeId;

            if (!string.IsNullOrWhiteSpace(input.Language))
            {
                body["textOptions"] = new System.Collections.Generic.Dictionary<string, object?>
                {
                    ["language"] = input.Language
                };
            }

            using var req = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/generations");
            req.Headers.TryAddWithoutValidation("X-API-KEY", _apiKey);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            req.Content = new StringContent(
                JsonSerializer.Serialize(body),
                Encoding.UTF8,
                "application/json");

            using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode)
                throw new InvalidOperationException($"HTTP {(int)resp.StatusCode}: {Truncate(json, 300)}");

            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("generationId", out var idEl))
                return idEl.GetString() ?? "";

            return "";
        }

        private async Task<GammaGenerationResult> PollUntilDoneAsync(string generationId, CancellationToken ct)
        {
            var deadline = DateTime.UtcNow + _pollTimeout;

            while (DateTime.UtcNow < deadline)
            {
                ct.ThrowIfCancellationRequested();
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);

                string json;
                try
                {
                    using var req = new HttpRequestMessage(HttpMethod.Get, $"{BaseUrl}/generations/{generationId}");
                    req.Headers.TryAddWithoutValidation("X-API-KEY", _apiKey);
                    req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                    using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
                    json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

                    if (!resp.IsSuccessStatusCode)
                        return GammaGenerationResult.Failed($"輪詢失敗 HTTP {(int)resp.StatusCode}: {Truncate(json, 200)}");
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    return GammaGenerationResult.Failed($"輪詢例外：{ex.Message}");
                }

                var parsed = ParseStatusResponse(json);

                if (parsed.IsTerminalSuccess)
                    return parsed;

                if (parsed.IsTerminalFailure)
                    return GammaGenerationResult.Failed(
                        string.IsNullOrWhiteSpace(parsed.ErrorMessage) ? "Gamma 生成失敗。" : parsed.ErrorMessage);

                // 其餘狀態（pending / processing…）：繼續輪詢。
            }

            return GammaGenerationResult.Failed($"Gamma 生成逾時（超過 {_pollTimeout.TotalMinutes:0} 分鐘）。");
        }

        // 依 Gamma API v1.0：{ generationId, status, gammaId, gammaUrl, exportUrl, credits:{deducted,remaining} }
        private static GammaGenerationResult ParseStatusResponse(string json)
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string status = GetString(root, "status").ToLowerInvariant();
                string gammaUrl = GetString(root, "gammaUrl");
                string exportUrl = GetString(root, "exportUrl");

                int creditsDeducted = 0;
                int creditsRemaining = -1;
                if (root.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
                {
                    if (credits.TryGetProperty("deducted", out var d) && d.TryGetInt32(out var dv)) creditsDeducted = dv;
                    if (credits.TryGetProperty("remaining", out var r) && r.TryGetInt32(out var rv)) creditsRemaining = rv;
                }

                bool completed = status is "completed" or "complete" or "succeeded" or "success";
                bool failed = status is "failed" or "error";

                return new GammaGenerationResult
                {
                    Success = completed,
                    Status = status,
                    GammaUrl = gammaUrl,
                    ExportUrl = exportUrl,
                    CreditsDeducted = creditsDeducted,
                    CreditsRemaining = creditsRemaining,
                    IsTerminalSuccess = completed,
                    IsTerminalFailure = failed,
                    ErrorMessage = failed ? GetString(root, "message") : ""
                };
            }
            catch (Exception ex)
            {
                return GammaGenerationResult.Failed($"解析 Gamma 回應失敗：{ex.Message}");
            }
        }

        private static string GetString(JsonElement obj, string name)
            => obj.TryGetProperty(name, out var el) && el.ValueKind == JsonValueKind.String
                ? (el.GetString() ?? "")
                : "";

        private static string Truncate(string s, int max)
            => string.IsNullOrEmpty(s) ? "" : (s.Length <= max ? s : s.Substring(0, max) + "…");
    }

    /// <summary>送給 Gamma 的生成輸入（Claude 已產好的內容 + 簡報設定）。</summary>
    public sealed class GammaGenerationInput
    {
        // Claude 產出的簡報內容（Gamma 以此設計版面）。
        public string InputText { get; init; } = "";
        public string Title { get; init; } = "";
        public int NumCards { get; init; }
        public string AdditionalInstructions { get; init; } = "";
        public string ThemeId { get; init; } = "";
        public string Language { get; init; } = "zh-tw";
        public string ExportAs { get; init; } = "pptx";
    }

    /// <summary>Gamma 生成結果。</summary>
    public sealed class GammaGenerationResult
    {
        public bool Success { get; init; }
        public string Status { get; init; } = "";
        public string GammaUrl { get; init; } = "";
        public string ExportUrl { get; init; } = "";
        public int CreditsDeducted { get; init; }
        public int CreditsRemaining { get; init; } = -1;
        public string ErrorMessage { get; init; } = "";

        // 內部輪詢用：是否為終態。
        public bool IsTerminalSuccess { get; init; }
        public bool IsTerminalFailure { get; init; }

        public bool HasExport => !string.IsNullOrWhiteSpace(ExportUrl);

        public static GammaGenerationResult NotConfigured() =>
            new GammaGenerationResult { Success = false, Status = "not_configured", ErrorMessage = "GAMMA_API_KEY 未設定。" };

        public static GammaGenerationResult Failed(string error) =>
            new GammaGenerationResult { Success = false, Status = "failed", IsTerminalFailure = true, ErrorMessage = error ?? "" };
    }
}
