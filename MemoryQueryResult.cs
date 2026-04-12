using System.Collections.Generic;

namespace test
{
    public sealed class MemoryQueryResult
    {
        public IReadOnlyList<MemoryItem> Items { get; init; } = new List<MemoryItem>();

        public IReadOnlyList<MemoryItem> AgentItems { get; init; } = new List<MemoryItem>();
        public IReadOnlyList<MemoryItem> SharedItems { get; init; } = new List<MemoryItem>();

        public string PromptBlock { get; init; } = "";
    }
}