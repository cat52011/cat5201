using System;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class PerplexitySonarProvider : IAiProvider
    {
        private readonly AiServiceRouter _router;

        public PerplexitySonarProvider(AiServiceRouter router)
        {
            _router = router;
        }

        public AiProviderKind Kind => AiProviderKind.PerplexitySonar;

        public bool Supports(AiRouteInfo route)
        {
            return route != null && route.Provider == AiProviderKind.PerplexitySonar;
        }

        public async Task<AiResponse> GenerateAsync(
            AiRequest request,
            CancellationToken ct = default)
        {
            var route = _router.GetRouteInfo(request.ModelId);
            var svc = _router.GetPerplexitySonarService(route.ServiceModel);

            string text = await svc.GenerateAsync(
                request.SystemPrompt,
                request.UserPrompt,
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
            var svc = _router.GetPerplexitySonarService(route.ServiceModel);

            string text = await svc.GenerateStreamAsync(
                request.SystemPrompt,
                request.UserPrompt,
                onDelta,
                request.MaxOutputTokens,
                ct).ConfigureAwait(false);

            return AiResponse.Success(
                text: text,
                modelUsed: route.NodeModel,
                providerUsed: Kind);
        }
    }
}