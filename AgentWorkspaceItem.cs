using System;

namespace test
{
    public sealed class AgentWorkspaceItem
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public string RunId { get; init; } = "";

        public string NodeId { get; init; } = "";

        public string SourceAgentId { get; init; } = "";

        public string ItemType { get; init; } = "";
        // search_summary / file_summary / code_analysis / reasoning_analysis / task_plan / delegate_output

        public string Title { get; init; } = "";

        public object? Payload { get; init; }

        public string TextSummary { get; init; } = "";

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }
}