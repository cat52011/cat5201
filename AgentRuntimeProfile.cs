namespace test
{
    public sealed class AgentRuntimeProfile
    {
        public string AgentId { get; init; } = "";
        public string RuntimeModelId { get; init; } = "";
        public NodeTaskMode RuntimeTaskMode { get; init; } = NodeTaskMode.Chat;
        public string SystemPrompt { get; init; } = "";
    }
}