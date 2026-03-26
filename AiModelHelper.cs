using System;

namespace test
{
    public static class AiModelHelper
    {
        public static bool IsOpenAiModel(string model)
            => !string.IsNullOrWhiteSpace(model) &&
               string.Equals(model.Trim(), AiModels.OpenAi_Gpt54, StringComparison.OrdinalIgnoreCase);

        public static bool IsClaudeModel(string model)
            => !string.IsNullOrWhiteSpace(model) &&
               (string.Equals(model.Trim(), AiModels.Claude_Sonnet46, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.Trim(), AiModels.Claude_Opus46, StringComparison.OrdinalIgnoreCase));

        public static bool IsPerplexitySonarModel(string model)
            => !string.IsNullOrWhiteSpace(model) &&
               (string.Equals(model.Trim(), AiModels.Perplexity_Sonar, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(model.Trim(), AiModels.Perplexity_SonarDeepResearch, StringComparison.OrdinalIgnoreCase));

        public static bool IsPerplexityDeepResearchModel(string model)
            => !string.IsNullOrWhiteSpace(model) &&
               string.Equals(model.Trim(), AiModels.Perplexity_SonarDeepResearch, StringComparison.OrdinalIgnoreCase);

        public static string MapPerplexitySonarModel(string model)
        {
            model = NormalizeNodeModel(model);

            return model switch
            {
                AiModels.Perplexity_Sonar => "sonar",
                AiModels.Perplexity_SonarDeepResearch => "sonar-deep-research",
                _ => AiModels.DefaultPerplexitySonarApiModel
            };
        }

        public static string NormalizeNodeModel(string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return AiModels.DefaultNodeModel;

            var m = model.Trim();

            if (string.Equals(m, AiModels.OpenAi_Gpt54, StringComparison.OrdinalIgnoreCase))
                return AiModels.OpenAi_Gpt54;

            if (string.Equals(m, AiModels.Claude_Sonnet46, StringComparison.OrdinalIgnoreCase))
                return AiModels.Claude_Sonnet46;

            if (string.Equals(m, AiModels.Claude_Opus46, StringComparison.OrdinalIgnoreCase))
                return AiModels.Claude_Opus46;

            if (string.Equals(m, AiModels.Perplexity_Sonar, StringComparison.OrdinalIgnoreCase))
                return AiModels.Perplexity_Sonar;

            if (string.Equals(m, AiModels.Perplexity_SonarDeepResearch, StringComparison.OrdinalIgnoreCase))
                return AiModels.Perplexity_SonarDeepResearch;

            return AiModels.DefaultNodeModel;
        }

        public static AiProviderKind GetProviderKind(string? model)
        {
            var normalized = NormalizeNodeModel(model);

            if (IsClaudeModel(normalized))
                return AiProviderKind.Claude;

            if (IsPerplexitySonarModel(normalized))
                return AiProviderKind.PerplexitySonar;

            if (IsOpenAiModel(normalized))
                return AiProviderKind.OpenAI;

            return AiProviderKind.Unknown;
        }

        public static AiRouteInfo BuildRouteInfo(string? model)
        {
            var normalized = NormalizeNodeModel(model);

            if (IsClaudeModel(normalized))
            {
                return new AiRouteInfo
                {
                    NodeModel = normalized,
                    Provider = AiProviderKind.Claude,
                    ServiceModel = normalized,
                    IsDeepResearch = false
                };
            }

            if (IsPerplexitySonarModel(normalized))
            {
                return new AiRouteInfo
                {
                    NodeModel = normalized,
                    Provider = AiProviderKind.PerplexitySonar,
                    ServiceModel = MapPerplexitySonarModel(normalized),
                    IsDeepResearch = IsPerplexityDeepResearchModel(normalized)
                };
            }

            return new AiRouteInfo
            {
                NodeModel = AiModels.OpenAi_Gpt54,
                Provider = AiProviderKind.OpenAI,
                ServiceModel = AiModels.OpenAi_Gpt54,
                IsDeepResearch = false
            };
        }
    }
}