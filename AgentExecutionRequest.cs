namespace test
{
    public sealed class AgentExecutionRequest
    {
        public NodeControl Node { get; init; } = null!;
        public AgentDefinition Agent { get; init; } = null!;

        public string TopText { get; init; } = "";
        public bool UseStreaming { get; init; }
        public System.Action<string>? OnDelta { get; init; }

        public int DelegationDepth { get; init; }

        public System.Threading.CancellationToken CancellationToken { get; init; }
        public bool ForceAgentProfile { get; init; }
        public bool SkipCapabilities { get; init; }
        public AgentWorkspace? Workspace { get; init; }
    }
}