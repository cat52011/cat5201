using System;

namespace test
{
    public static class AiModelHelper
    {
        public static string NormalizeNodeModel(string? model)
        {
            var def = AiModelRegistry.Find(model);

            // 休眠中的擴充點（IsAvailable=false，如尚未啟用的 Gemini）一律退回預設模型，
            // 避免實際路由到沒有 provider 實作的模型。
            if (def == null || !def.IsAvailable)
                return AiModelRegistry.Default.Id;

            return def.Id;
        }

        public static AiModelDefinition GetDefinition(string? model)
        {
            return AiModelRegistry.Find(model) ?? AiModelRegistry.Default;
        }

        public static bool IsOpenAiModel(string model)
            => GetDefinition(model).Provider == AiProviderType.OpenAI;

        public static bool IsClaudeModel(string model)
            => GetDefinition(model).Provider == AiProviderType.Claude;

        public static bool IsPerplexitySonarModel(string model)
            => GetDefinition(model).Provider == AiProviderType.Perplexity;

        public static bool IsPerplexityDeepResearchModel(string model)
            => GetDefinition(model).IsDeepResearch;

        public static string MapPerplexitySonarModel(string model)
        {
            var def = GetDefinition(model);
            return string.IsNullOrWhiteSpace(def.ServiceModel)
                ? AiModels.DefaultPerplexitySonarApiModel
                : def.ServiceModel;
        }

        public static AiProviderKind GetProviderKind(string? model)
        {
            var def = GetDefinition(model);

            return def.Provider switch
            {
                AiProviderType.OpenAI => AiProviderKind.OpenAI,
                AiProviderType.Claude => AiProviderKind.Claude,
                AiProviderType.Perplexity => AiProviderKind.PerplexitySonar,
                AiProviderType.Gemini => AiProviderKind.Gemini, // 休眠：router 尚無 case，需配合 GeminiProvider 實作
                _ => AiProviderKind.Unknown
            };
        }

        public static AiRouteInfo BuildRouteInfo(string? model)
        {
            var def = GetDefinition(model);

            return new AiRouteInfo
            {
                NodeModel = def.Id,
                Provider = GetProviderKind(def.Id),
                ServiceModel = string.IsNullOrWhiteSpace(def.ServiceModel) ? def.Id : def.ServiceModel,
                IsDeepResearch = def.IsDeepResearch
            };
        }
    }
}
