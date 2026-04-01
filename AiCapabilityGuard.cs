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

            if (!SupportsAll(resolved, requiredCaps))
            {
                string fallback = FindBestCapabilityMatchedModel(taskMode, requested, requiredCaps);

                if (!string.Equals(fallback, resolved, StringComparison.OrdinalIgnoreCase))
                {
                    resolved = fallback;
                    modelAdjusted = true;
                }

                if (hasImageAttachments && !AiModelHelper.GetDefinition(requested).SupportsImages)
                    reasons.Add("偵測到圖片附件，原模型不支援 Images");

                if (hasFileAttachments && !AiModelHelper.GetDefinition(requested).SupportsFiles)
                    reasons.Add("偵測到檔案附件，原模型不支援 Files");

                if (requireSearchCapability && !AiModelHelper.GetDefinition(requested).SupportsSearch)
                    reasons.Add("目前任務需要 Search，原模型不支援 Search");
            }

            bool streamingAllowed = wantsStreaming;
            if (wantsStreaming && !AiModelHelper.GetDefinition(resolved).SupportsStreaming)
            {
                streamingAllowed = false;
                streamingAdjusted = true;
                reasons.Add("目前模型不支援 Streaming，已自動改為非串流模式");
            }

            return new AiCapabilityCheckResult
            {
                RequestedModelId = requested,
                ResolvedModelId = resolved,
                ModelAdjusted = modelAdjusted,
                StreamingAdjusted = streamingAdjusted,
                StreamingAllowed = streamingAllowed,
                Reason = reasons.Count == 0
                    ? ""
                    : string.Join("；", reasons)
            };
        }

        private static bool SupportsAll(string modelId, AiModelCapability requiredCaps)
        {
            if (requiredCaps == AiModelCapability.None)
                return true;

            var def = AiModelHelper.GetDefinition(modelId);
            return (def.Capabilities & requiredCaps) == requiredCaps;
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

            foreach (var def in AiModelRegistry.All)
                Add(def.Id);

            return result;
        }
    }
}