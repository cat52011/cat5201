namespace test
{
    public sealed class AgentDelegationTraceItem
    {
        public int Depth { get; init; }

        public string FromAgentId { get; init; } = "";
        public string ToAgentId { get; init; } = "";

        public string Instruction { get; init; } = "";
        public string OutputSummary { get; init; } = "";

        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
    }
}