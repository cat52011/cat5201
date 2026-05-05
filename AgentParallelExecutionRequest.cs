using System.Collections.Generic;

namespace test
{
    public sealed class AgentParallelExecutionRequest
    {
        public NodeControl Node { get; init; } = null!;

        public string OriginalInput { get; init; } = "";

        public IReadOnlyList<AgentParallelTask> Tasks { get; init; }
            = new List<AgentParallelTask>();

        public AgentWorkspace Workspace { get; init; } = null!;
    }

    public sealed class AgentParallelTask
    {
        public string AgentId { get; init; } = "";

        public string Instruction { get; init; } = "";

        public string Purpose { get; init; } = "";
    }
}