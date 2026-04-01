using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class ClaudeProvider : IAiProvider
    {
        private readonly AiServiceRouter _router;

        public ClaudeProvider(AiServiceRouter router)
        {
            _router = router;
        }

        public AiProviderKind Kind => AiProviderKind.Claude;

        public bool Supports(AiRouteInfo route)
        {
            return route != null && route.Provider == AiProviderKind.Claude;
        }

        public async Task<AiResponse> GenerateAsync(
            AiRequest request,
            CancellationToken ct = default)
        {
            var route = _router.GetRouteInfo(request.ModelId);
            var svc = _router.GetClaudeService(route.ServiceModel);

            var contentBlocks = await BuildContentBlocksAsync(request, ct).ConfigureAwait(false);
            string text = await svc.GenerateAsync(
                request.SystemPrompt,
                contentBlocks,
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
            var svc = _router.GetClaudeService(route.ServiceModel);

            var contentBlocks = await BuildContentBlocksAsync(request, ct).ConfigureAwait(false);
            string text = await svc.GenerateStreamAsync(
                request.SystemPrompt,
                contentBlocks,
                onDelta,
                request.MaxOutputTokens,
                ct).ConfigureAwait(false);

            return AiResponse.Success(
                text: text,
                modelUsed: route.NodeModel,
                providerUsed: Kind);
        }

        private static async Task<List<object>> BuildContentBlocksAsync(AiRequest request, CancellationToken ct)
        {
            var blocks = new List<object>();

            foreach (var a in request.Attachments)
            {
                if (string.IsNullOrWhiteSpace(a.AbsolutePath) || !File.Exists(a.AbsolutePath))
                    continue;

                byte[] bytes = await File.ReadAllBytesAsync(a.AbsolutePath, ct).ConfigureAwait(false);

                if (a.IsImage)
                {
                    blocks.Add(ClaudeChatService.BuildImageBlock(bytes, a.MimeType));
                    continue;
                }

                if (string.Equals(a.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                {
                    blocks.Add(ClaudeChatService.BuildPdfBlock(bytes));
                    continue;
                }

                string text;
                try
                {
                    text = Encoding.UTF8.GetString(bytes);
                }
                catch
                {
                    text = $"[無法以 UTF-8 讀取附件：{a.FileName}]";
                }

                blocks.Add(ClaudeChatService.BuildTextBlock(
                    $"【附件：{a.FileName}】\n{text}"));
            }

            blocks.Add(ClaudeChatService.BuildTextBlock(request.UserPrompt ?? ""));
            return blocks;
        }
    }
}