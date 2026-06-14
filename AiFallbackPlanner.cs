using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class AiFallbackPlanner
    {
        public static IReadOnlyList<(string ModelId, string Reason)> BuildCandidates(
            string primaryModelId,
            NodeTaskMode taskMode)
            => BuildCandidates(primaryModelId, taskMode, allowExpensiveAutoModels: false);

        public static IReadOnlyList<(string ModelId, string Reason)> BuildCandidates(
            string primaryModelId,
            NodeTaskMode taskMode,
            bool allowExpensiveAutoModels)
        {
            var result = new List<(string ModelId, string Reason)>();

            void Add(string? modelId, string reason)
            {
                if (string.IsNullOrWhiteSpace(modelId))
                    return;

                string normalized = AiModelHelper.NormalizeNodeModel(modelId);

                if (!allowExpensiveAutoModels && AiAutoCostPolicy.IsExpensiveAutoModel(normalized))
                    return;

                if (result.Any(x => string.Equals(x.ModelId, normalized, StringComparison.OrdinalIgnoreCase)))
                    return;

                result.Add((normalized, reason));
            }

            string primary = AiModelHelper.NormalizeNodeModel(primaryModelId);

            Add(primary, "primary");

            foreach (var sameProvider in GetSameProviderFallbacks(primary))
                Add(sameProvider, "same-provider fallback");

            foreach (var preferred in NodeTaskRoutingRegistry.GetPreferredModelIds(taskMode))
                Add(preferred, "task-preferred fallback");

            Add(AiModelRegistry.Default.Id, "default fallback");

            // Multi-Model v1：最後手段依成本由便宜到貴，且只含已啟用模型。
            foreach (var def in AiModelRegistry.Available.OrderBy(x => (int)x.CostTier))
                Add(def.Id, "last-resort fallback");

            return result;
        }

        private static IReadOnlyList<string> GetSameProviderFallbacks(string modelId)
        {
            modelId = AiModelHelper.NormalizeNodeModel(modelId);

            if (string.Equals(modelId, AiModels.Claude_Opus46, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    AiModels.Claude_Sonnet46
                };
            }

            if (string.Equals(modelId, AiModels.Claude_Sonnet46, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    AiModels.Claude_Opus46
                };
            }

            if (string.Equals(modelId, AiModels.Perplexity_Sonar, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    AiModels.Perplexity_SonarDeepResearch
                };
            }

            if (string.Equals(modelId, AiModels.Perplexity_SonarDeepResearch, StringComparison.OrdinalIgnoreCase))
            {
                return new[]
                {
                    AiModels.Perplexity_Sonar
                };
            }

            return Array.Empty<string>();
        }
    }
}
