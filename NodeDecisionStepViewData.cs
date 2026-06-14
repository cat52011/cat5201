using System.Collections.Generic;

namespace test
{
    public enum NodeDecisionStepState
    {
        Info = 0,
        Success = 1,
        Warning = 2,
        Error = 3
    }

    public sealed class NodeDecisionStepViewData
    {
        public string Title { get; init; } = "";
        public string Detail { get; init; } = "";
        public NodeDecisionStepState State { get; init; } = NodeDecisionStepState.Info;
        public bool Highlight { get; init; }

        // 新增：目前是否為執行中的 active step
        public bool IsActive { get; init; }

        // 可展開明細
        public IReadOnlyList<string> DetailLines { get; init; }
            = new List<string>();

        // Workspace v2：Workspace step 直接帶結構化 artifact 紀錄，
        // 讓 UI 渲染成產品化卡片而非 re-parse 文字行（debug dump）。
        public IReadOnlyList<AgentWorkspaceArtifactRecord> WorkspaceArtifacts { get; init; }
            = new List<AgentWorkspaceArtifactRecord>();

        public bool IsExpandable =>
            (DetailLines != null && DetailLines.Count > 0) ||
            (WorkspaceArtifacts != null && WorkspaceArtifacts.Count > 0);
    }
}