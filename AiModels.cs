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

        public const string DefaultPerplexitySonarApiModel = "sonar";

        public static string DefaultNodeModel => AiModelRegistry.Default.Id;
        public static string DefaultOpenAiModel => OpenAi_Gpt54;
        public static string DefaultClaudeModel => Claude_Sonnet46;

        public static IReadOnlyList<string> AllNodeModels =>
            AiModelRegistry.All.Select(x => x.Id).ToList();

        public static bool IsKnownNodeModel(string? model)
            => AiModelRegistry.IsKnown(model);
    }
}