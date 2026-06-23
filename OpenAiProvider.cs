using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class OpenAiProvider : IAiProvider
    {
        private readonly AiServiceRouter _router;

        public OpenAiProvider(AiServiceRouter router)
        {
            _router = router;
        }

        public AiProviderKind Kind => AiProviderKind.OpenAI;

        public bool Supports(AiRouteInfo route)
        {
            return route != null && route.Provider == AiProviderKind.OpenAI;
        }

        public async Task<AiResponse> GenerateAsync(
            AiRequest request,
            CancellationToken ct = default)
        {
            var route = _router.GetRouteInfo(request.ModelId);
            var svc = _router.GetOpenAiService(route.ServiceModel);

            object input = await BuildInputAsync(request, ct).ConfigureAwait(false);
            int capturedIn = 0, capturedOut = 0;
            string text = await svc.GenerateAsync(
                request.SystemPrompt,
                input,
                request.MaxOutputTokens,
                ct,
                onUsage: (i, o) => { capturedIn = i; capturedOut = o; }).ConfigureAwait(false);

            return AiResponse.Success(
                text: text,
                modelUsed: route.NodeModel,
                providerUsed: Kind,
                inputTokens: capturedIn > 0 ? capturedIn : (int?)null,
                outputTokens: capturedOut > 0 ? capturedOut : (int?)null);
        }

        public async Task<AiResponse> GenerateStreamAsync(
            AiRequest request,
            Action<string>? onDelta,
            CancellationToken ct = default)
        {
            var route = _router.GetRouteInfo(request.ModelId);
            var svc = _router.GetOpenAiService(route.ServiceModel);

            object input = await BuildInputAsync(request, ct).ConfigureAwait(false);
            int capturedIn = 0, capturedOut = 0;
            string text = await svc.GenerateStreamAsync(
                request.SystemPrompt,
                input,
                onDelta,
                request.MaxOutputTokens,
                ct,
                onUsage: (i, o) => { capturedIn = i; capturedOut = o; }).ConfigureAwait(false);

            return AiResponse.Success(
                text: text,
                modelUsed: route.NodeModel,
                providerUsed: Kind,
                inputTokens: capturedIn > 0 ? capturedIn : (int?)null,
                outputTokens: capturedOut > 0 ? capturedOut : (int?)null);
        }

        private static async Task<object> BuildInputAsync(AiRequest request, CancellationToken ct)
        {
            var content = new List<object>
            {
                new { type = "input_text", text = request.UserPrompt ?? "" }
            };

            foreach (var a in request.Attachments)
            {
                if (string.IsNullOrWhiteSpace(a.AbsolutePath) || !File.Exists(a.AbsolutePath))
                    continue;

                byte[] bytes = await File.ReadAllBytesAsync(a.AbsolutePath, ct).ConfigureAwait(false);
                string dataUrl = ToDataUrl(bytes, a.MimeType);

                if (a.IsImage)
                {
                    content.Add(new { type = "input_image", image_url = dataUrl });
                }
                else if (IsOpenAiSupportedFileType(a.MimeType))
                {
                    // OpenAI Responses API 只接受 pdf / text 類附件；其他格式跳過避免 400。
                    content.Add(new { type = "input_file", filename = a.FileName, file_data = dataUrl });
                }
                // docx / pptx / xlsx / octet-stream 等不支援類型 → 略過，不送給 OpenAI。
            }

            return new object[]
            {
                new
                {
                    role = "user",
                    content = content.ToArray()
                }
            };
        }

        private static string ToDataUrl(byte[] bytes, string mimeType)
            => $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";

        // OpenAI Responses API input_file 支援的 MIME 類型白名單。
        // 不在此清單的附件（docx/pptx/xlsx/octet-stream）直接跳過，避免 400 invalid_value。
        private static bool IsOpenAiSupportedFileType(string? mimeType)
        {
            if (string.IsNullOrWhiteSpace(mimeType)) return false;
            string m = mimeType.ToLowerInvariant();
            return m.StartsWith("text/", StringComparison.Ordinal)
                || m == "application/pdf"
                || m == "application/json";
        }
    }
}