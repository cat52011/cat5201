using System;
using System.Collections.Generic;

namespace test
{
    public sealed class AgentWorkspaceItem
    {
        public string Id { get; init; } = Guid.NewGuid().ToString("N");

        public string RunId { get; init; } = "";

        public string NodeId { get; init; } = "";

        public string SourceAgentId { get; init; } = "";

        public string ItemType { get; init; } = "";
        // search_summary / file_summary / code_analysis / reasoning_analysis / task_plan / delegate_output

        public string ArtifactKind { get; init; } = "";
        // facts / search / analysis / code / patch / diff / media / sandbox / final

        public string ContentFormat { get; init; } = "";
        // text / markdown / json / code / image / video / binary

        public bool IsUserVisible { get; init; } = true;

        public string Title { get; init; } = "";

        // Workspace v2：source metadata + status + dependencies。
        // 空字串時由 AgentWorkspace.BuildArtifactRecords 依 payload 型別集中推導，
        // 因此各 capability 不必每處都填，schema 仍維持一致。
        public string ModelId { get; init; } = "";

        public string CapabilityId { get; init; } = "";

        public string Status { get; init; } = "";
        // ArtifactStatus: draft / ready / validated / exported / failed

        public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();
        // 上游 artifact 的 ItemType（或 Id），用來顯示產出物之間的依賴。

        public object? Payload { get; init; }

        // settable：orchestration_plan 等長生命週期 artifact 會在執行結束時刷新摘要
        public string TextSummary { get; set; } = "";

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }
}
