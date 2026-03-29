using System.Collections.Generic;

namespace test
{
    public sealed class NodeTaskRoutingProfile
    {
        public NodeTaskMode Mode { get; init; } = NodeTaskMode.Chat;

        public string DisplayName { get; init; } = "";

        public string Description { get; init; } = "";

        // 依優先順序排列的正式 Node Model Id
        public IReadOnlyList<string> PreferredModelIds { get; init; } = new List<string>();

        // 該任務偏好的能力
        public AiModelCapability PreferredCapabilities { get; init; } = AiModelCapability.None;

        // 該任務是否傾向搜尋型模型
        public bool PrefersSearch { get; init; }

        // 該任務是否傾向長上下文
        public bool PrefersLongContext { get; init; }

        // 該任務是否傾向深度研究
        public bool PrefersDeepResearch { get; init; }

        // 備註說明，可供未來 debug / UI 顯示
        public string Notes { get; init; } = "";
    }
}