using System.Collections.Generic;

namespace test
{
    public sealed class MemoryQueryResult
    {
        public IReadOnlyList<MemoryItem> Items { get; init; } = new List<MemoryItem>();

        public IReadOnlyList<MemoryItem> AgentItems { get; init; } = new List<MemoryItem>();
        public IReadOnlyList<MemoryItem> SharedItems { get; init; } = new List<MemoryItem>();

        // episodic 記憶區塊（相關度召回）。
        public string PromptBlock { get; init; } = "";

        // 使用者偏好區塊（一律注入、最高優先），與 episodic 分開讓 prompt 放在最上方當硬指令。
        public string PreferenceBlock { get; init; } = "";
    }
}