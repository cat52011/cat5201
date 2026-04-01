using System;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public interface IAiProvider
    {
        AiProviderKind Kind { get; }

        bool Supports(AiRouteInfo route);

        Task<AiResponse> GenerateAsync(
            AiRequest request,
            CancellationToken ct = default);

        Task<AiResponse> GenerateStreamAsync(
            AiRequest request,
            Action<string>? onDelta,
            CancellationToken ct = default);
    }
}