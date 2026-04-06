using System.Collections.Generic;

namespace test
{
    public sealed class MemoryQueryResult
    {
        public IReadOnlyList<MemoryItem> Items { get; init; } = new List<MemoryItem>();

        public string PromptBlock { get; init; } = "";
    }
}