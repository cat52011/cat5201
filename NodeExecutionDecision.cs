using System;
using System.Collections.Generic;

namespace test
{
    public sealed class NodeExecutionDecision
    {
        public string RequestedAgentId { get; set; } = "";
        public string ActualAgentId { get; set; } = "";

        public string RequestedModelId { get; set; } = "";
        public string ModelId { get; set; } = "";
        public string ActualModelId { get; set; } = "";

        public NodeTaskMode TaskMode { get; set; } = NodeTaskMode.Chat;

        public string ResolverLabel { get; set; } = "Manual";
        public string StatusLabel { get; set; } = "Manual";

        public double Confidence { get; set; }

        public string ResolverReason { get; set; } = "";
        public IReadOnlyList<string> ResolverKeywords { get; set; } = Array.Empty<string>();

        public bool UsedApiResolver { get; set; }
        public bool UsedFallbackToRules { get; set; }

        public bool UseStreaming { get; set; } = true;

        public bool CapabilityAdjusted { get; set; }
        public string CapabilityReason { get; set; } = "";

        public string CapabilityRequestedModelId { get; set; } = "";
        public string CapabilityResolvedModelId { get; set; } = "";
        public AiModelCapability CapabilityRequired { get; set; } = AiModelCapability.None;
        public AiModelCapability CapabilityMissing { get; set; } = AiModelCapability.None;
        public bool CapabilityStreamingAdjusted { get; set; }

        public bool RuntimeFallbackUsed { get; set; }
        public string RuntimeFallbackSummary { get; set; } = "";

        public IReadOnlyList<AgentCapabilityTraceItem> CapabilityTrace { get; set; }
            = Array.Empty<AgentCapabilityTraceItem>();

        public IReadOnlyList<AgentDelegationTraceItem> DelegationTrace { get; set; }
            = Array.Empty<AgentDelegationTraceItem>();

        public IReadOnlyList<AiFallbackAttempt> RuntimeFallbackAttempts { get; set; }
            = Array.Empty<AiFallbackAttempt>();
        public bool ForceSingleModel { get; set; }
        public string WorkspaceSummary { get; set; } = "";
        public IReadOnlyList<string> WorkspaceArtifactDetails { get; set; }
            = Array.Empty<string>();
        public IReadOnlyList<AgentWorkspaceArtifactRecord> WorkspaceArtifacts { get; set; }
            = Array.Empty<AgentWorkspaceArtifactRecord>();

        // Memory v1 視覺化：本次召回的偏好 / 記憶統計。
        public MemoryRecallStats MemoryRecall { get; set; } = MemoryRecallStats.Empty;

        // §6 第一層輸出判斷的白話摘要（報告 / 表格 / 簡報 / 純文字），給決策窗顯示。
        public string OutputIntentSummary { get; set; } = "";
    }
}
