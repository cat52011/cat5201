using System;

namespace test
{
    public sealed class MemoryItem
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public MemoryScope Scope { get; init; } = MemoryScope.Project;

        public string Category { get; init; } = "";
        // 例如：
        // execution_result / summary / fact / preference / source_note / extracted_data

        public string FileKey { get; init; } = "";
        public string SourceNodeId { get; init; } = "";

        public string Title { get; init; } = "";
        public string Content { get; init; } = "";

        public string[] Tags { get; init; } = Array.Empty<string>();

        public string TaskMode { get; init; } = "";
        public string ModelId { get; init; } = "";

        public double Importance { get; init; } = 0.5;

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
        public DateTime UpdatedAtUtc { get; init; } = DateTime.UtcNow;

        public string AgentId { get; init; } = "";
        public bool IsSharedMemory { get; init; }
    }
}