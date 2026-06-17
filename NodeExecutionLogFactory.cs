using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public sealed class NodeExecutionLogFactory
    {
        public AiExecutionLogEntry Create(
            NodeControl node,
            NodeExecutionDecision decision,
            DateTime startedAtUtc,
            bool success,
            string errorMessage = "")
        {
            var endedAtUtc = DateTime.UtcNow;
            long durationMs = Math.Max(0, (long)(endedAtUtc - startedAtUtc).TotalMilliseconds);

            string actualModelId = AiModelHelper.NormalizeNodeModel(
                string.IsNullOrWhiteSpace(decision.ActualModelId)
                    ? decision.ModelId
                    : decision.ActualModelId);

            // Token / 成本：優先用 API 回傳的真實 usage（含系統提示/記憶/上下文，數字才準）；
            // 該模型尚未接真實 usage 時，退回用節點輸入（top）/輸出（bottom）字元數推估。
            var costEst = node.TryGetRealTokenUsage(out int realIn, out int realOut)
                ? ModelCostEstimator.FromUsage(actualModelId, realIn, realOut)
                : ModelCostEstimator.Compute(actualModelId, node.GetTopText(), node.GetBottomText());

            return new AiExecutionLogEntry
            {
                NodeId = node.Id.ToString(),

                InputTokens = costEst.InputTokens,
                OutputTokens = costEst.OutputTokens,
                CostDisplay = costEst.Display,

                StartedAtUtc = startedAtUtc,
                EndedAtUtc = endedAtUtc,
                DurationMs = durationMs,

                SelectionMode = GetSelectionModeLabel(decision),
                Resolver = decision.ResolverLabel ?? "",

                RequestedModelId = AiModelHelper.NormalizeNodeModel(decision.RequestedModelId),
                PlannedModelId = AiModelHelper.NormalizeNodeModel(decision.ModelId),
                ActualModelId = AiModelHelper.NormalizeNodeModel(
                    string.IsNullOrWhiteSpace(decision.ActualModelId)
                        ? decision.ModelId
                        : decision.ActualModelId),

                TaskMode = NodeTaskModeHelper.ToDisplayName(decision.TaskMode),
                Confidence = decision.Confidence,

                ResolverReason = decision.ResolverReason ?? "",
                ResolverKeywords = decision.ResolverKeywords?.ToList() ?? new List<string>(),
                WorkspaceSummary = decision.WorkspaceSummary ?? "",
                WorkspaceArtifactDetails = decision.WorkspaceArtifactDetails?.ToList() ?? new List<string>(),
                WorkspaceArtifacts = decision.WorkspaceArtifacts?.ToList() ?? new List<AgentWorkspaceArtifactRecord>(),
                CapabilityAdjusted = decision.CapabilityAdjusted,
                CapabilityReason = decision.CapabilityReason ?? "",

                CapabilityRequestedModelId = AiModelHelper.NormalizeNodeModel(decision.CapabilityRequestedModelId),
                CapabilityResolvedModelId = AiModelHelper.NormalizeNodeModel(decision.CapabilityResolvedModelId),
                CapabilityRequired = decision.CapabilityRequired.ToString(),
                CapabilityMissing = decision.CapabilityMissing.ToString(),
                CapabilityStreamingAdjusted = decision.CapabilityStreamingAdjusted,

                CapabilityTrace = decision.CapabilityTrace?.ToList() ?? new List<AgentCapabilityTraceItem>(),

                RuntimeFallbackUsed = decision.RuntimeFallbackUsed,
                RuntimeFallbackSummary = decision.RuntimeFallbackSummary ?? "",

                Success = success,
                ErrorMessage = errorMessage ?? "",

                FallbackAttempts = decision.RuntimeFallbackAttempts?.ToList() ?? new List<AiFallbackAttempt>(),
                RequestedAgentId = decision.RequestedAgentId ?? "",
                ActualAgentId = decision.ActualAgentId ?? "",

                MemoryRecall = decision.MemoryRecall ?? MemoryRecallStats.Empty,
                OutputIntentSummary = decision.OutputIntentSummary ?? "",
            };
        }

        private static string GetSelectionModeLabel(NodeExecutionDecision decision)
        {
            if (decision.UsedApiResolver)
                return "API Auto";

            if (string.Equals(decision.StatusLabel, "Rule Auto", StringComparison.OrdinalIgnoreCase))
                return "Auto";

            return "Manual";
        }
    }
}
