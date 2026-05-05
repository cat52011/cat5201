using System.Collections.Generic;

namespace test
{
    public sealed class AgentWorkspaceSummary
    {
        public string RunId { get; init; } = "";

        public IReadOnlyList<string> ItemTypes { get; init; }
            = new List<string>();

        public IReadOnlyList<string> SourceAgents { get; init; }
            = new List<string>();

        public string SummaryText { get; init; } = "";
    }
}