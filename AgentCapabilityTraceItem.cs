namespace test
{
    public sealed class AgentCapabilityTraceItem
    {
        public string CapabilityId { get; init; } = "";

        public string AgentId { get; init; } = "";

        public bool CanHandle { get; init; }

        public bool Executed { get; init; }

        public bool Handled { get; init; }

        public bool AugmentedPrompt { get; init; }

        public bool Success { get; init; } = true;

        public string Summary { get; init; } = "";

        public string ErrorMessage { get; init; } = "";
    }
}