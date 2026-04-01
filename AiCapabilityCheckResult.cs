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
    }
}