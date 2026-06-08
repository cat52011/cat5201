using System;

namespace test
{
    public sealed class AgentWorkspaceArtifactRecord
    {
        public string Id { get; init; } = "";

        public string RunId { get; init; } = "";

        public string NodeId { get; init; } = "";

        public string SourceAgentId { get; init; } = "";

        public string ItemType { get; init; } = "";

        public string ArtifactKind { get; init; } = "";

        public string ContentFormat { get; init; } = "";

        public bool IsUserVisible { get; init; }

        public string Title { get; init; } = "";

        public string Preview { get; init; } = "";

        public int EstimatedSize { get; init; }

        public int FactCount { get; init; }

        public DateTime CreatedAtUtc { get; init; }
    }
}
