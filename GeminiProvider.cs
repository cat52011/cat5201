using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    /// <summary>
    /// Multi-Model：Gemini provider（v1 文字）。透過 router 取得 GeminiChatService。
    /// v1 能力：文字 / 長文 / 程式碼（registry 未宣告 Images/Search，故 capability guard 不會把圖片/搜尋任務導到這裡）。
    /// 串流：底層先用非串流取得完整文字，再一次性 onDelta 輸出（正確性優先；逐字串流屬 v2）。
    /// </summary>
    public sealed class GeminiProvider : IAiProvider
    {
        private readonly AiServiceRouter _router;

        public GeminiProvider(AiServiceRouter router)
        {
            _router = router;
        }

        public AiProviderKind Kind => AiProviderKind.Gemini;

        public bool Supports(AiRouteInfo route)
        {
            return route != null && route.Provider == AiProviderKind.Gemini;
        }

        public async Task<AiResponse> GenerateAsync(AiRequest request, CancellationToken ct = default)
        {
            var route = _router.GetRouteInfo(request.ModelId);
            var svc = _router.GetGeminiService(route.ServiceModel);

            string userText = ComposePrompt(request);
            string text = await svc.GenerateAsync(
                request.SystemPrompt, userText, request.MaxOutputTokens, ct).ConfigureAwait(false);

            return AiResponse.Success(text, route.NodeModel, Kind);
        }

        public async Task<AiResponse> GenerateStreamAsync(
            AiRequest request, Action<string>? onDelta, CancellationToken ct = default)
        {
            var resp = await GenerateAsync(request, ct).ConfigureAwait(false);

            if (resp.IsSuccess && !string.IsNullOrEmpty(resp.Text))
                onDelta?.Invoke(resp.Text);

            return resp;
        }

        // v1：把文字附件併進 prompt；圖片附件略過（registry 未宣告 Images）。
        private static string ComposePrompt(AiRequest request)
        {
            var sb = new StringBuilder();

            foreach (var a in request.Attachments)
            {
                if (a == null || string.IsNullOrWhiteSpace(a.AbsolutePath) || !File.Exists(a.AbsolutePath))
                    continue;
                if (a.IsImage)
                    continue;

                try
                {
                    string t = File.ReadAllText(a.AbsolutePath);
                    sb.Append($"【附件：{a.FileName}】\n{t}\n\n");
                }
                catch
                {
                    // 讀不到的附件略過。
                }
            }

            sb.Append(request.UserPrompt ?? "");
            return sb.ToString();
        }
    }
}
