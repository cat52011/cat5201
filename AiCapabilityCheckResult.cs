using System.Collections.Generic;

namespace test
{
    public sealed class AiCapabilityCheckResult
    {
        public string RequestedModelId { get; init; } = "";
        public string ResolvedModelId { get; init; } = "";

        public bool ModelAdjusted { get; init; }
        public bool StreamingAdjusted { get; init; }

        public bool StreamingAllowed { get; init; } = true;

        public string Reason { get; init; } = "";

        public AiModelCapability RequiredCapabilities { get; init; } = AiModelCapability.None;
        public AiModelCapability MissingCapabilities { get; init; } = AiModelCapability.None;

        public IReadOnlyList<string> ReasonParts { get; init; } = new List<string>();
    }
}