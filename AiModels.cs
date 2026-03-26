using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class AiModels
    {
        public const string OpenAi_Gpt54 = "gpt-5.4";

        public const string Claude_Sonnet46 = "claude-sonnet-4-6";
        public const string Claude_Opus46 = "claude-opus-4-6";

        public const string Perplexity_Sonar = "pplx-sonar";
        public const string Perplexity_SonarDeepResearch = "pplx-sonar-deep-research";

        public const string DefaultNodeModel = OpenAi_Gpt54;
        public const string DefaultOpenAiModel = OpenAi_Gpt54;
        public const string DefaultClaudeModel = Claude_Sonnet46;
        public const string DefaultPerplexitySonarApiModel = "sonar";

        public static readonly IReadOnlyList<string> AllNodeModels = new[]
        {
            OpenAi_Gpt54,
            Claude_Sonnet46,
            Claude_Opus46,
            Perplexity_Sonar,
            Perplexity_SonarDeepResearch
        };

        public static bool IsKnownNodeModel(string? model)
        {
            if (string.IsNullOrWhiteSpace(model))
                return false;

            return AllNodeModels.Any(x => string.Equals(x, model.Trim(), StringComparison.OrdinalIgnoreCase));
        }
    }
}