using System.Collections.Generic;

namespace test
{
    /// <summary>
    /// Memory v1 視覺化：一次執行召回了哪些記憶 / 偏好，給 decision-viz 顯示用。
    /// 純計數 + 簡短說明，不含 prompt 區塊本身。
    /// </summary>
    public sealed class MemoryRecallStats
    {
        // 是否因規則（例如財經即時任務）而整體略過記憶注入。
        public bool Suppressed { get; init; }
        public string SuppressReason { get; init; } = "";

        public int PreferenceCount { get; init; }
        public int EpisodicCount { get; init; }
        public int AgentCount { get; init; }
        public int SharedCount { get; init; }

        // 簡短條列：「偏好：輸出語言…」「記憶：執行結果 · …」
        public IReadOnlyList<string> Details { get; init; } = new List<string>();

        public bool HasAny => PreferenceCount > 0 || EpisodicCount > 0;

        public static MemoryRecallStats Empty { get; } = new MemoryRecallStats();
    }
}
