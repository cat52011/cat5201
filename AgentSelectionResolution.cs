namespace test
{
    public sealed class AgentSelectionResolution
    {
        public string AgentId { get; init; } = "general-agent";

        public double Confidence { get; init; }

        public string Reason { get; init; } = "";
    }
}