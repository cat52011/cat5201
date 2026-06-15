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
            => BuildCandidates(primaryModelId, taskMode, applyUserCostBlock: false);

        public static IReadOnlyList<(string ModelId, string Reason)> BuildCandidates(
            string primaryModelId,
            NodeTaskMode taskMode,
            bool applyUserCostBlock)
        {
            var result = new List<(string ModelId, string Reason)>();

            void Add(string? modelId, string reason)
            {
                if (string.IsNullOrWhiteSpace(modelId))
                    return;

                string normalized = AiModelHelper.NormalizeNodeModel(modelId);

                // §15 個人化：fallback 候選的成本過濾，唯一依據是個人化開關。
                // 只有被使用者「明確關閉」的高成本模型才從候選鏈剔除；未關閉者一律保留，
                // 不再硬編碼把 Opus / Deep Research 當成永遠要避開的昂貴模型。
                if (applyUserCostBlock && AiAutoCostPolicy.TryEnforceUserBlock(normalized, out _))
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
