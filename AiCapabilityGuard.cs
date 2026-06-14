using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class AiCapabilityGuard
    {
        public static AiCapabilityCheckResult Resolve(
            string? requestedModelId,
            NodeTaskMode taskMode,
            bool wantsStreaming,
            bool hasImageAttachments,
            bool hasFileAttachments,
            bool requireSearchCapability)
        {
            string requested = AiModelHelper.NormalizeNodeModel(requestedModelId);
            string resolved = requested;

            bool modelAdjusted = false;
            bool streamingAdjusted = false;

            var reasons = new List<string>();

            var requiredCaps = AiModelCapability.None;

            if (hasImageAttachments)
                requiredCaps |= AiModelCapability.Images;

            if (hasFileAttachments)
                requiredCaps |= AiModelCapability.Files;

            if (requireSearchCapability)
                requiredCaps |= AiModelCapability.Search;

            var requestedDef = AiModelHelper.GetDefinition(requested);
            var missingCaps = requiredCaps & ~requestedDef.Capabilities;

            if (missingCaps != AiModelCapability.None)
            {
                string fallback = FindBestCapabilityMatchedModel(taskMode, requested, requiredCaps);

                if (!string.Equals(fallback, resolved, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = fallback;
                    modelAdjusted = true;
                }

                if (missingCaps.HasFlag(AiModelCapability.Images))
                    reasons.Add("Images 不支援");

                if (missingCaps.HasFlag(AiModelCapability.Files))
                    reasons.Add("Files 不支援");

                if (missingCaps.HasFlag(AiModelCapability.Search))
                    reasons.Add("Search 不支援");
            }

            bool streamingAllowed = wantsStreaming;
            if (wantsStreaming && !AiModelHelper.GetDefinition(resolved).SupportsStreaming)
            {
                streamingAllowed = false;
                streamingAdjusted = true;
                reasons.Add("Streaming 不支援，已改為 non-streaming");
            }

            string finalReason = reasons.Count == 0
                ? ""
                : string.Join(" / ", reasons);

            return new AiCapabilityCheckResult
            {
                RequestedModelId = requested,
                ResolvedModelId = resolved,
                ModelAdjusted = modelAdjusted,
                StreamingAdjusted = streamingAdjusted,
                StreamingAllowed = streamingAllowed,
                Reason = finalReason,
                RequiredCapabilities = requiredCaps,
                MissingCapabilities = missingCaps,
                ReasonParts = reasons
            };
        }

        private static string FindBestCapabilityMatchedModel(
            NodeTaskMode taskMode,
            string currentModelId,
            AiModelCapability requiredCaps)
        {
            var ordered = BuildCandidateOrder(taskMode, currentModelId);

            foreach (var modelId in ordered)
            {
                if (SupportsAll(modelId, requiredCaps))
                    return AiModelHelper.NormalizeNodeModel(modelId);
            }

            return AiModelRegistry.Default.Id;
        }

        private static bool SupportsAll(string modelId, AiModelCapability requiredCaps)
        {
            if (requiredCaps == AiModelCapability.None)
                return true;

            var def = AiModelHelper.GetDefinition(modelId);
            return (def.Capabilities & requiredCaps) == requiredCaps;
        }

        private static IReadOnlyList<string> BuildCandidateOrder(
            NodeTaskMode taskMode,
            string currentModelId)
        {
            var result = new List<string>();

            void Add(string? id)
            {
                if (string.IsNullOrWhiteSpace(id))
                    return;

                string normalized = AiModelHelper.NormalizeNodeModel(id);

                if (!result.Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase)))
                    result.Add(normalized);
            }

            foreach (var preferred in NodeTaskRoutingRegistry.GetPreferredModelIds(taskMode))
                Add(preferred);

            Add(currentModelId);

            // Multi-Model v1：依能力重導時，剩餘候選依成本層級由便宜到貴排序，
            // 只納入已啟用的模型（休眠擴充點不選），達成「依能力＋成本」的挑選。
            foreach (var def in AiModelRegistry.Available.OrderBy(x => (int)x.CostTier))
                Add(def.Id);

            return result;
        }
    }
}