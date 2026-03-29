using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class NodeTaskRoutingRegistry
    {
        private static readonly IReadOnlyList<NodeTaskRoutingProfile> _all = new[]
        {
            new NodeTaskRoutingProfile
            {
                Mode = NodeTaskMode.Chat,
                DisplayName = "Chat",
                Description = "一般對話 / 綜合型任務",
                PreferredModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46,
                    AiModels.Claude_Opus46,
                    AiModels.Perplexity_Sonar
                },
                PreferredCapabilities =
                    AiModelCapability.Streaming |
                    AiModelCapability.LongContext,
                PrefersSearch = false,
                PrefersLongContext = true,
                PrefersDeepResearch = false,
                Notes = "一般對話以穩定、通用、可延展為主。"
            },

            new NodeTaskRoutingProfile
            {
                Mode = NodeTaskMode.Research,
                DisplayName = "Research",
                Description = "查證 / 搜尋 / 最新資訊 / 比較分析",
                PreferredModelIds = new[]
                {
                    AiModels.Perplexity_SonarDeepResearch,
                    AiModels.Perplexity_Sonar,
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46
                },
                PreferredCapabilities =
                    AiModelCapability.Search |
                    AiModelCapability.LongContext |
                    AiModelCapability.Streaming,
                PrefersSearch = true,
                PrefersLongContext = true,
                PrefersDeepResearch = true,
                Notes = "Research 優先搜尋能力，其次長上下文與整理能力。"
            },

            new NodeTaskRoutingProfile
            {
                Mode = NodeTaskMode.Translate,
                DisplayName = "Translate",
                Description = "翻譯 / 語言轉換 / 菜單翻譯 / 對照輸出",
                PreferredModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46,
                    AiModels.Claude_Opus46
                },
                PreferredCapabilities =
                    AiModelCapability.Files |
                    AiModelCapability.Images |
                    AiModelCapability.LongContext |
                    AiModelCapability.Streaming,
                PrefersSearch = false,
                PrefersLongContext = true,
                PrefersDeepResearch = false,
                Notes = "Translate 優先多模態、文件理解與穩定輸出品質。"
            },

            new NodeTaskRoutingProfile
            {
                Mode = NodeTaskMode.Summarize,
                DisplayName = "Summarize",
                Description = "摘要 / 重點整理 / 濃縮",
                PreferredModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46,
                    AiModels.Perplexity_Sonar
                },
                PreferredCapabilities =
                    AiModelCapability.LongContext |
                    AiModelCapability.Streaming,
                PrefersSearch = false,
                PrefersLongContext = true,
                PrefersDeepResearch = false,
                Notes = "Summarize 優先長上下文與輸出穩定性。"
            },

            new NodeTaskRoutingProfile
            {
                Mode = NodeTaskMode.Rewrite,
                DisplayName = "Rewrite",
                Description = "改寫 / 潤稿 / 語氣調整 / 重寫",
                PreferredModelIds = new[]
                {
                    AiModels.Claude_Sonnet46,
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Opus46
                },
                PreferredCapabilities =
                    AiModelCapability.LongContext |
                    AiModelCapability.Streaming,
                PrefersSearch = false,
                PrefersLongContext = true,
                PrefersDeepResearch = false,
                Notes = "Rewrite 優先語氣控制、文字細緻度與長文連貫性。"
            },

            new NodeTaskRoutingProfile
            {
                Mode = NodeTaskMode.Extract,
                DisplayName = "Extract",
                Description = "抽取欄位 / 提取資訊 / 結構化整理",
                PreferredModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46,
                    AiModels.Perplexity_Sonar
                },
                PreferredCapabilities =
                    AiModelCapability.Files |
                    AiModelCapability.Images |
                    AiModelCapability.LongContext |
                    AiModelCapability.Streaming,
                PrefersSearch = false,
                PrefersLongContext = true,
                PrefersDeepResearch = false,
                Notes = "Extract 優先文件理解、抽取穩定性與結構化能力。"
            },

            new NodeTaskRoutingProfile
            {
                Mode = NodeTaskMode.Code,
                DisplayName = "Code",
                Description = "程式 / 除錯 / 架構修改 / 可直接貼上",
                PreferredModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Opus46,
                    AiModels.Claude_Sonnet46
                },
                PreferredCapabilities =
                    AiModelCapability.Files |
                    AiModelCapability.LongContext |
                    AiModelCapability.Streaming,
                PrefersSearch = false,
                PrefersLongContext = true,
                PrefersDeepResearch = false,
                Notes = "Code 優先程式正確性、長上下文與大型修改能力。"
            }
        };

        public static IReadOnlyList<NodeTaskRoutingProfile> All => _all;

        public static NodeTaskRoutingProfile Default =>
            Get(NodeTaskMode.Chat);

        public static NodeTaskRoutingProfile Get(NodeTaskMode mode)
        {
            mode = NodeTaskModeHelper.Normalize(mode);

            return _all.FirstOrDefault(x => x.Mode == mode)
                   ?? _all.First(x => x.Mode == NodeTaskMode.Chat);
        }

        public static IReadOnlyList<string> GetPreferredModelIds(NodeTaskMode mode)
        {
            return Get(mode).PreferredModelIds
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(AiModelHelper.NormalizeNodeModel)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<AiModelDefinition> GetPreferredModelDefinitions(NodeTaskMode mode)
        {
            return GetPreferredModelIds(mode)
                .Select(AiModelRegistry.Find)
                .Where(x => x != null)
                .Cast<AiModelDefinition>()
                .ToList();
        }

        public static bool IsPreferredModel(NodeTaskMode mode, string? modelId)
        {
            if (string.IsNullOrWhiteSpace(modelId))
                return false;

            var normalized = AiModelHelper.NormalizeNodeModel(modelId);

            return GetPreferredModelIds(mode)
                .Any(x => string.Equals(x, normalized, StringComparison.OrdinalIgnoreCase));
        }

        public static string RecommendModel(NodeTaskMode mode, string? currentSelectedModel = null)
        {
            var normalizedCurrent = AiModelHelper.NormalizeNodeModel(currentSelectedModel);

            // 如果目前手動選的模型本來就在此 TaskMode 偏好內，就保留它
            if (IsPreferredModel(mode, normalizedCurrent))
                return normalizedCurrent;

            // 否則取第一個可用的偏好模型
            foreach (var modelId in GetPreferredModelIds(mode))
            {
                if (AiModelRegistry.IsKnown(modelId))
                    return modelId;
            }

            // 最後 fallback
            return normalizedCurrent;
        }

        public static AiModelCapability GetPreferredCapabilities(NodeTaskMode mode)
        {
            return Get(mode).PreferredCapabilities;
        }

        public static bool PrefersSearch(NodeTaskMode mode)
        {
            return Get(mode).PrefersSearch;
        }

        public static bool PrefersLongContext(NodeTaskMode mode)
        {
            return Get(mode).PrefersLongContext;
        }

        public static bool PrefersDeepResearch(NodeTaskMode mode)
        {
            return Get(mode).PrefersDeepResearch;
        }

        public static string BuildDebugSummary(NodeTaskMode mode)
        {
            var profile = Get(mode);

            string models = string.Join(", ", profile.PreferredModelIds);
            if (string.IsNullOrWhiteSpace(models))
                models = "(none)";

            return
                $"TaskMode = {profile.DisplayName}\n" +
                $"Description = {profile.Description}\n" +
                $"PreferredModels = {models}\n" +
                $"PreferredCapabilities = {profile.PreferredCapabilities}\n" +
                $"PrefersSearch = {profile.PrefersSearch}\n" +
                $"PrefersLongContext = {profile.PrefersLongContext}\n" +
                $"PrefersDeepResearch = {profile.PrefersDeepResearch}\n" +
                $"Notes = {profile.Notes}";
        }

        public static bool MatchesPreferredCapabilities(NodeTaskMode mode, string? modelId)
        {
            var def = AiModelRegistry.Find(modelId);
            if (def == null)
                return false;

            var required = GetPreferredCapabilities(mode);

            if (required == AiModelCapability.None)
                return true;

            // 只要至少命中一項偏好能力，就算有對到方向
            return (def.Capabilities & required) != AiModelCapability.None;
        }

        public static IReadOnlyList<string> GetCapabilityMatchedModels(NodeTaskMode mode)
        {
            var required = GetPreferredCapabilities(mode);

            if (required == AiModelCapability.None)
                return AiModelRegistry.All.Select(x => x.Id).ToList();

            return AiModelRegistry.All
                .Where(x => (x.Capabilities & required) != AiModelCapability.None)
                .Select(x => x.Id)
                .ToList();
        }
    }
}