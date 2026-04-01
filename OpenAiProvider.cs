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
            string text = await svc.GenerateAsync(
                request.SystemPrompt,
                input,
                request.MaxOutputTokens,
                ct).ConfigureAwait(false);

            return AiResponse.Success(
                text: text,
                modelUsed: route.NodeModel,
                providerUsed: Kind);
        }

        public async Task<AiResponse> GenerateStreamAsync(
            AiRequest request,
            Action<string>? onDelta,
            CancellationToken ct = default)
        {
            var route = _router.GetRouteInfo(request.ModelId);
            var svc = _router.GetOpenAiService(route.ServiceModel);

            object input = await BuildInputAsync(request, ct).ConfigureAwait(false);
            string text = await svc.GenerateStreamAsync(
                request.SystemPrompt,
                input,
                onDelta,
                request.MaxOutputTokens,
                ct).ConfigureAwait(false);

            return AiResponse.Success(
                text: text,
                modelUsed: route.NodeModel,
                providerUsed: Kind);
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
                    content.Add(new
                    {
                        type = "input_image",
                        image_url = dataUrl
                    });
                }
                else
                {
                    content.Add(new
                    {
                        type = "input_file",
                        filename = a.FileName,
                        file_data = dataUrl
                    });
                }
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
        {
            return $"data:{mimeType};base64,{Convert.ToBase64String(bytes)}";
        }
    }
}