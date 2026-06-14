using System;
using System.Collections.Generic;

namespace test
{
    /// <summary>
    /// 一次工作流執行的歷史紀錄（§3 Workflow replay / rerun / resume 用）。
    /// 由 NodeService 在每次節點執行結束時，從 workspace 的 orchestration_plan 取得各 step 狀態後建立。
    /// </summary>
    public sealed class WorkflowRunRecord
    {
        public string RunId { get; init; } = Guid.NewGuid().ToString("N");

        public Guid NodeId { get; init; }

        /// <summary>本次執行是哪一種觸發：初次 / 重播 / 從失敗續跑 / 只重生答案。</summary>
        public WorkflowRunKind Kind { get; init; } = WorkflowRunKind.Initial;

        public DateTime StartedAtUtc { get; init; } = DateTime.UtcNow;

        public DateTime FinishedAtUtc { get; init; } = DateTime.UtcNow;

        /// <summary>本次執行使用的完整輸入（replay / regenerate 會原樣重跑，所以不截斷）。</summary>
        public string OriginalInput { get; init; } = "";

        public bool Success { get; init; }

        /// <summary>orchestration 整體狀態：success / partial / failed。</summary>
        public string OverallStatus { get; init; } = "";

        public string OutputPreview { get; init; } = "";

        public string ErrorMessage { get; init; } = "";

        /// <summary>resume 時：第一個尚未成功（失敗 / 跳過 / 未跑）的 step id。</summary>
        public string ResumedFromStepId { get; init; } = "";

        public IReadOnlyList<WorkflowRunStep> Steps { get; init; } = Array.Empty<WorkflowRunStep>();

        public string InputPreview =>
            string.IsNullOrEmpty(OriginalInput)
                ? ""
                : (OriginalInput.Length <= 160 ? OriginalInput : OriginalInput.Substring(0, 160) + "…");

        public TimeSpan Duration =>
            FinishedAtUtc > StartedAtUtc ? FinishedAtUtc - StartedAtUtc : TimeSpan.Zero;
    }

    public sealed class WorkflowRunStep
    {
        public int Order { get; init; }
        public string Id { get; init; } = "";
        public string Label { get; init; } = "";
        public string Status { get; init; } = "";
        public string Detail { get; init; } = "";
    }

    public enum WorkflowRunKind
    {
        /// <summary>使用者第一次跑（或一般送出）。</summary>
        Initial,

        /// <summary>用上一次的相同輸入，整段重新執行一次。</summary>
        Replay,

        /// <summary>上一次失敗 / 部分失敗，重新嘗試整段工作流。</summary>
        Resume,

        /// <summary>沿用上一次成功的 workspace（research / capability 成果），只重新產生最終答案。</summary>
        RegenerateAnswer
    }

    public static class WorkflowRunKindText
    {
        public static string ToLabel(WorkflowRunKind kind) => kind switch
        {
            WorkflowRunKind.Initial => "初次執行",
            WorkflowRunKind.Replay => "重播",
            WorkflowRunKind.Resume => "從失敗續跑",
            WorkflowRunKind.RegenerateAnswer => "重新生成答案",
            _ => kind.ToString()
        };
    }
}
