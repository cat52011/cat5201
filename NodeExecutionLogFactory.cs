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

            return new AiExecutionLogEntry
            {
                NodeId = node.Id.ToString(),

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

                CapabilityAdjusted = decision.CapabilityAdjusted,
                CapabilityReason = decision.CapabilityReason ?? "",

                CapabilityRequestedModelId = AiModelHelper.NormalizeNodeModel(decision.CapabilityRequestedModelId),
                CapabilityResolvedModelId = AiModelHelper.NormalizeNodeModel(decision.CapabilityResolvedModelId),
                CapabilityRequired = decision.CapabilityRequired.ToString(),
                CapabilityMissing = decision.CapabilityMissing.ToString(),
                CapabilityStreamingAdjusted = decision.CapabilityStreamingAdjusted,

                RuntimeFallbackUsed = decision.RuntimeFallbackUsed,
                RuntimeFallbackSummary = decision.RuntimeFallbackSummary ?? "",

                Success = success,
                ErrorMessage = errorMessage ?? "",

                FallbackAttempts = decision.RuntimeFallbackAttempts?.ToList() ?? new List<AiFallbackAttempt>(),
                RequestedAgentId = decision.RequestedAgentId ?? "",
                ActualAgentId = decision.ActualAgentId ?? "",
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