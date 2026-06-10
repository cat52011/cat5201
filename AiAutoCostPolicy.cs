using System;

namespace test
{
    public static class AiAutoCostPolicy
    {
        public static string NormalizeForAuto(string? modelId)
        {
            string raw = (modelId ?? "").Trim();
            if (string.Equals(raw, "sonar-deep-research", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(raw, AiModels.Perplexity_SonarDeepResearch, StringComparison.OrdinalIgnoreCase))
            {
                return AiModels.Perplexity_Sonar;
            }

            if (string.Equals(raw, AiModels.Claude_Opus46, StringComparison.OrdinalIgnoreCase))
                return AiModels.Claude_Sonnet46;

            string normalized = AiModelHelper.NormalizeNodeModel(modelId);
            var def = AiModelRegistry.Find(normalized);

            if (def == null)
                return normalized;

            return def.Provider switch
            {
                AiProviderType.Claude => AiModels.Claude_Sonnet46,
                AiProviderType.Perplexity => AiModels.Perplexity_Sonar,
                _ => normalized
            };
        }

        public static bool IsExpensiveAutoModel(string? modelId)
        {
            string normalized = AiModelHelper.NormalizeNodeModel(modelId);

            return string.Equals(normalized, AiModels.Claude_Opus46, StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, AiModels.Perplexity_SonarDeepResearch, StringComparison.OrdinalIgnoreCase);
        }
    }
}
