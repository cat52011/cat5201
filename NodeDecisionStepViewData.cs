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

        public bool IsExpandable =>
            DetailLines != null && DetailLines.Count > 0;
    }
}