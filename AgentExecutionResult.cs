using System.Collections.Generic;

namespace test
{
    public sealed class AgentExecutionResult
    {
        public NodeExecutionDecision Decision { get; init; } = new();
        public AiFallbackExecutionResult Execution { get; init; } = new();

        public IReadOnlyList<AgentDelegationTraceItem> DelegationTrace { get; init; }
            = new List<AgentDelegationTraceItem>();

        public string FinalText =>
            Execution?.Text ?? "";

        public bool IsSuccess =>
            Execution != null && Execution.IsSuccess;
    }
}