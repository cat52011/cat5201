namespace test
{
    public sealed class AgentMemoryPolicy
    {
        public bool EnableSessionMemory { get; init; } = true;
        public bool EnableAgentMemory { get; init; } = true;
        public bool EnableSharedMemory { get; init; } = true;

        public int RecallMaxItems { get; init; } = 6;

        public static AgentMemoryPolicy Default { get; } = new();
    }
}