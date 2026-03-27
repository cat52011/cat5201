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
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files | AiModelCapability.LongContext,
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
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files | AiModelCapability.LongContext,
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
                Capabilities = AiModelCapability.Streaming | AiModelCapability.Images | AiModelCapability.Files | AiModelCapability.LongContext,
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
                IsDefaultNodeModel = false,
                ServiceModel = "sonar-deep-research",
                IsDeepResearch = true
            }
        };

        public static IReadOnlyList<AiModelDefinition> All => _all;

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
    }
}