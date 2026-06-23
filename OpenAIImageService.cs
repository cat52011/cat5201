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
    /// Image Gen v1：最小可用的 OpenAI Images API（DALL-E 3）封裝。
    /// 直接以 b64_json 取回 PNG bytes，避免再下載一次圖片。
    /// API Key 讀取：環境變數 OPENAI_API_KEY（與 OpenAIChatService 一致）。
    /// </summary>
    public sealed class OpenAIImageService
    {
        private static readonly HttpClient _http = new HttpClient
        {
            // 生成圖片可能要數十秒，交給 CancellationToken 控制，不用固定短 timeout。
            Timeout = Timeout.InfiniteTimeSpan
        };

        private readonly string _apiKey;
        private readonly string _model;

        public OpenAIImageService(string model = "gpt-image-2")
        {
            _model = model;
            _apiKey = ApiKeyStore.Resolve("OPENAI_API_KEY");

            if (string.IsNullOrWhiteSpace(_apiKey))
            {
                throw new InvalidOperationException(
                    "找不到 OPENAI_API_KEY。請先在系統環境變數設定 OPENAI_API_KEY，或在啟動前注入到程序環境。");
            }
        }

        public sealed class ImageResult
        {
            public bool Success { get; init; }
            public byte[] PngBytes { get; init; } = Array.Empty<byte>();
            public string RevisedPrompt { get; init; } = "";
            public string Size { get; init; } = "";
            public string ErrorMessage { get; init; } = "";
        }

        /// <summary>
        /// 生成單張圖片。size 支援 1024x1024 / 1792x1024 / 1024x1792（DALL-E 3）。
        /// </summary>
        public async Task<ImageResult> GenerateAsync(
            string prompt,
            string size = "1024x1024",
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(prompt))
            {
                return new ImageResult { Success = false, ErrorMessage = "圖片描述為空。" };
            }

            // 注意：新版 Images API 不接受 response_format 參數，移除以免 400。
            // dall-e-3 預設回傳 url，gpt-image-1 預設回傳 b64_json；下方解析兩者都支援。
            // 品質：gpt-image 系列預設品質偏低，明確帶 quality=high 才會輸出高品質圖（dall-e-3 用 hd）。
            bool isGptImage = _model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase);
            bool isDalle3 = _model.StartsWith("dall-e-3", StringComparison.OrdinalIgnoreCase);

            object payload = isGptImage
                ? new { model = _model, prompt, n = 1, size, quality = "high" }
                : isDalle3
                    ? (object)new { model = _model, prompt, n = 1, size, quality = "hd" }
                    : new { model = _model, prompt, n = 1, size };

            try
            {
                using var req = new HttpRequestMessage(
                    HttpMethod.Post, "https://api.openai.com/v1/images/generations");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                req.Content = new StringContent(
                    // payload 宣告為 object（三元運算），明確帶入 runtime 型別確保匿名物件欄位被序列化。
                    JsonSerializer.Serialize(payload, payload.GetType()),
                    Encoding.UTF8,
                    "application/json");

                using var resp = await _http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct);

                string body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                {
                    return new ImageResult
                    {
                        Success = false,
                        ErrorMessage = $"OpenAI Images API {(int)resp.StatusCode}：{Truncate(body, 400)}"
                    };
                }

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array ||
                    data.GetArrayLength() == 0)
                {
                    return new ImageResult
                    {
                        Success = false,
                        ErrorMessage = "OpenAI Images API 回傳沒有 data。"
                    };
                }

                var first = data[0];

                string revised = first.TryGetProperty("revised_prompt", out var revEl)
                    ? (revEl.GetString() ?? "")
                    : "";

                // 優先用 b64_json；若回傳的是 url，則再下載一次取得 PNG bytes。
                byte[] bytes;
                string b64 = first.TryGetProperty("b64_json", out var b64El)
                    ? (b64El.GetString() ?? "")
                    : "";

                if (!string.IsNullOrWhiteSpace(b64))
                {
                    bytes = Convert.FromBase64String(b64);
                }
                else
                {
                    string url = first.TryGetProperty("url", out var urlEl)
                        ? (urlEl.GetString() ?? "")
                        : "";

                    if (string.IsNullOrWhiteSpace(url))
                    {
                        return new ImageResult
                        {
                            Success = false,
                            ErrorMessage = "OpenAI Images API 回傳沒有 b64_json 也沒有 url。"
                        };
                    }

                    using var imgResp = await _http.GetAsync(url, ct);
                    if (!imgResp.IsSuccessStatusCode)
                    {
                        return new ImageResult
                        {
                            Success = false,
                            ErrorMessage = $"下載圖片失敗：HTTP {(int)imgResp.StatusCode}"
                        };
                    }

                    bytes = await imgResp.Content.ReadAsByteArrayAsync(ct);
                }

                return new ImageResult
                {
                    Success = true,
                    PngBytes = bytes,
                    RevisedPrompt = revised,
                    Size = size
                };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ImageResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        /// <summary>
        /// 圖片編輯（image-to-image）：吃一張輸入圖 + 編輯指令，輸出改過的圖。
        /// 用 OpenAI /v1/images/edits（multipart）；edits 端點以 gpt-image-1 最穩定。
        /// 用於「上傳照片 → 依指令改建 / 重新裝潢 / 改圖」，盡量保留原圖整體結構。
        /// </summary>
        public async Task<ImageResult> EditAsync(
            byte[] imageBytes,
            string prompt,
            string size = "1024x1024",
            CancellationToken ct = default)
        {
            if (imageBytes == null || imageBytes.Length == 0)
                return new ImageResult { Success = false, ErrorMessage = "沒有輸入圖片可供編輯。" };
            if (string.IsNullOrWhiteSpace(prompt))
                return new ImageResult { Success = false, ErrorMessage = "編輯描述為空。" };

            try
            {
                using var form = new MultipartFormDataContent();
                form.Add(new StringContent("gpt-image-1"), "model");
                form.Add(new StringContent(prompt), "prompt");
                form.Add(new StringContent(size), "size");
                form.Add(new StringContent("high"), "quality");

                var imgContent = new ByteArrayContent(imageBytes);
                imgContent.Headers.ContentType = new MediaTypeHeaderValue("image/png");
                form.Add(imgContent, "image", "input.png");

                using var req = new HttpRequestMessage(
                    HttpMethod.Post, "https://api.openai.com/v1/images/edits");
                req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);
                req.Content = form;

                using var resp = await _http.SendAsync(
                    req, HttpCompletionOption.ResponseHeadersRead, ct);

                string body = await resp.Content.ReadAsStringAsync(ct);

                if (!resp.IsSuccessStatusCode)
                    return new ImageResult
                    {
                        Success = false,
                        ErrorMessage = $"OpenAI Images Edit API {(int)resp.StatusCode}：{Truncate(body, 400)}"
                    };

                using var doc = JsonDocument.Parse(body);
                if (!doc.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind != JsonValueKind.Array || data.GetArrayLength() == 0)
                    return new ImageResult { Success = false, ErrorMessage = "OpenAI Images Edit API 回傳沒有 data。" };

                var first = data[0];
                string revised = first.TryGetProperty("revised_prompt", out var revEl) ? (revEl.GetString() ?? "") : "";
                string b64 = first.TryGetProperty("b64_json", out var b64El) ? (b64El.GetString() ?? "") : "";

                byte[] bytes;
                if (!string.IsNullOrWhiteSpace(b64))
                {
                    bytes = Convert.FromBase64String(b64);
                }
                else
                {
                    string url = first.TryGetProperty("url", out var urlEl) ? (urlEl.GetString() ?? "") : "";
                    if (string.IsNullOrWhiteSpace(url))
                        return new ImageResult { Success = false, ErrorMessage = "OpenAI Images Edit API 回傳沒有 b64_json 也沒有 url。" };

                    using var imgResp = await _http.GetAsync(url, ct);
                    if (!imgResp.IsSuccessStatusCode)
                        return new ImageResult { Success = false, ErrorMessage = $"下載編輯後圖片失敗：HTTP {(int)imgResp.StatusCode}" };
                    bytes = await imgResp.Content.ReadAsByteArrayAsync(ct);
                }

                return new ImageResult { Success = true, PngBytes = bytes, RevisedPrompt = revised, Size = size };
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                return new ImageResult { Success = false, ErrorMessage = ex.Message };
            }
        }

        private static string Truncate(string s, int max)
        {
            s ??= "";
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }
    }
}
