using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class AiModelRegistry
    {
        private static readonly IReadOnlyList<AiModelDefinition> _all = new[]
        {
            new AiModelDefinition
            {
                Id = "gpt-5.4",
                DisplayName = "GPT-5.4",
                IconPath = "pack://application:,,,/Assets/OpenAI_logo.png",
                Provider = AiProviderType.OpenAI,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files
                    | AiModelCapability.LongContext | AiModelCapability.ImageGeneration | AiModelCapability.Code,
                CostTier = AiCostTier.Standard,
                IsDefaultNodeModel = true,
                ServiceModel = "gpt-5.4",
                IsDeepResearch = false
            },
            new AiModelDefinition
            {
                Id = "claude-sonnet-4-6",
                DisplayName = "Claude Sonnet 4.6",
                IconPath = "pack://application:,,,/Assets/Claude_logo.png",
                Provider = AiProviderType.Claude,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files
                    | AiModelCapability.LongContext | AiModelCapability.Code,
                CostTier = AiCostTier.Standard,
                IsDefaultNodeModel = false,
                ServiceModel = "claude-sonnet-4-6",
                IsDeepResearch = false
            },
            new AiModelDefinition
            {
                Id = "claude-opus-4-6",
                DisplayName = "Claude Opus 4.6",
                IconPath = "pack://application:,,,/Assets/Claude_logo.png",
                Provider = AiProviderType.Claude,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files
                    | AiModelCapability.LongContext | AiModelCapability.Code,
                CostTier = AiCostTier.Premium,
                IsDefaultNodeModel = false,
                ServiceModel = "claude-opus-4-6",
                IsDeepResearch = false
            },
            new AiModelDefinition
            {
                Id = "pplx-sonar",
                DisplayName = "Perplexity Sonar",
                IconPath = "pack://application:,,,/Assets/Perplexity_logo.png",
                Provider = AiProviderType.Perplexity,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Search,
                CostTier = AiCostTier.Economy,
                IsDefaultNodeModel = false,
                ServiceModel = "sonar",
                IsDeepResearch = false
            },
            new AiModelDefinition
            {
                Id = "pplx-sonar-deep-research",
                DisplayName = "Perplexity Deep Research",
                IconPath = "pack://application:,,,/Assets/Perplexity_logo.png",
                Provider = AiProviderType.Perplexity,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Search | AiModelCapability.LongContext,
                CostTier = AiCostTier.Standard,
                IsDefaultNodeModel = false,
                ServiceModel = "sonar-deep-research",
                IsDeepResearch = true
            },

            // Multi-Model v1：Gemini 已啟用（GeminiProvider + GeminiChatService + router case 已接線）。
            // v1 能力宣告為文字 / 長文 / 程式碼（未宣告 Images/Search，避免把圖片/搜尋任務導到尚未實作的路徑）。
            new AiModelDefinition
            {
                Id = "gemini-2.5-flash",
                DisplayName = "Gemini 2.5 Flash",
                IconPath = "pack://application:,,,/Assets/Gemini_logo.png",
                Provider = AiProviderType.Gemini,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.LongContext | AiModelCapability.Code,
                CostTier = AiCostTier.Economy,
                IsAvailable = true,
                IsDefaultNodeModel = false,
                ServiceModel = "gemini-2.5-flash",
                IsDeepResearch = false
            },
            new AiModelDefinition
            {
                Id = "gemini-2.5-pro",
                DisplayName = "Gemini 2.5 Pro",
                IconPath = "pack://application:,,,/Assets/Gemini_logo.png",
                Provider = AiProviderType.Gemini,
                Capabilities = AiModelCapability.Streaming | AiModelCapability.LongContext | AiModelCapability.Code,
                CostTier = AiCostTier.Standard,
                IsAvailable = true,
                IsDefaultNodeModel = false,
                ServiceModel = "gemini-2.5-pro",
                IsDeepResearch = false
            }
        };

        public static IReadOnlyList<AiModelDefinition> All => _all;

        /// <summary>只含已啟用的模型（休眠擴充點不列入）。UI 選單、fallback、capability 重導都用這個。</summary>
        public static IReadOnlyList<AiModelDefinition> Available =>
            _all.Where(x => x.IsAvailable).ToList();

        public static AiModelDefinition Default =>
            _all.First(x => x.IsDefaultNodeModel);

        public static AiModelDefinition? Find(string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return null;

            return _all.FirstOrDefault(x =>
                string.Equals(x.Id, modelId.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static bool IsKnown(string? modelId)
            => Find(modelId) != null;

        public static bool IsAvailable(string? modelId)
            => Find(modelId)?.IsAvailable == true;

        /// <summary>列出具備指定能力、已啟用的模型，依成本層級由便宜到貴排序。</summary>
        public static IReadOnlyList<AiModelDefinition> WithCapability(AiModelCapability required)
        {
            return _all
                .Where(x => x.IsAvailable && (x.Capabilities & required) == required)
                .OrderBy(x => (int)x.CostTier)
                .ToList();
        }

        /// <summary>在具備指定能力的已啟用模型中，挑成本最低者；找不到回 null。</summary>
        public static AiModelDefinition? CheapestWithCapability(AiModelCapability required)
            => WithCapability(required).FirstOrDefault();
    }
}
