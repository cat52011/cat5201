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
                PreferredModelIds = Array.Empty<string>(),
                PreferredCapabilities =
                    AiModelCapability.Streaming |
                    AiModelCapability.LongContext,
                PrefersSearch = false,
                PrefersLongContext = true,
                PrefersDeepResearch = false,
                Notes = "Chat 不強制指定固定首選模型，優先保留目前節點手動模型；若沒有可用模型則回退到 gpt-5.5。"
            },

            new NodeTaskRoutingProfile
            {
                Mode = NodeTaskMode.Research,
                DisplayName = "Research",
                Description = "查證 / 搜尋 / 最新資訊 / 比較分析",
                PreferredModelIds = new[]
                {
                    AiModels.Perplexity_Sonar,
                    AiModels.Perplexity_SonarDeepResearch,
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46
                },
                PreferredCapabilities =
                    AiModelCapability.Search |
                    AiModelCapability.Streaming,
                PrefersSearch = true,
                PrefersLongContext = false,
                PrefersDeepResearch = false,
                Notes = "Research 首選 pplx-sonar，次選 pplx-sonar-deep-research，理由是搜尋 / 查證 / 最新資訊。"
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
                Notes = "Translate 首選 gpt-5.5，次選 claude-sonnet-4-6。"
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
                Notes = "Summarize 首選 gpt-5.5，次選 claude-sonnet-4-6。"
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
                Notes = "Rewrite 首選 claude-sonnet-4-6，次選 gpt-5.5。"
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
                Notes = "Extract 首選 gpt-5.5，次選 claude-sonnet-4-6。"
            },

            new NodeTaskRoutingProfile
            {
                Mode = NodeTaskMode.Code,
                DisplayName = "Code",
                Description = "程式 / 除錯 / 架構修改 / 可直接貼上",
                PreferredModelIds = new[]
                {
                    AiModels.Claude_Opus46,
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46
                },
                PreferredCapabilities =
                    AiModelCapability.Files |
                    AiModelCapability.LongContext |
                    AiModelCapability.Streaming,
                PrefersSearch = false,
                PrefersLongContext = true,
                PrefersDeepResearch = false,
                Notes = "Code 首選 claude-opus-4-8，次選 gpt-5.5。"
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
            mode = NodeTaskModeHelper.Normalize(mode);

            return mode switch
            {
                NodeTaskMode.Research => AiModels.Perplexity_Sonar,
                NodeTaskMode.Translate => AiModels.OpenAi_Gpt54,
                NodeTaskMode.Code => AiModels.Claude_Opus46,
                NodeTaskMode.Summarize => AiModels.OpenAi_Gpt54,
                NodeTaskMode.Rewrite => AiModels.Claude_Sonnet46,
                NodeTaskMode.Extract => AiModels.OpenAi_Gpt54,

                NodeTaskMode.Chat => AiModelRegistry.IsKnown(currentSelectedModel)
                    ? AiModelHelper.NormalizeNodeModel(currentSelectedModel)
                    : AiModels.OpenAi_Gpt54,

                _ => AiModels.OpenAi_Gpt54
            };
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

            string models = profile.PreferredModelIds != null && profile.PreferredModelIds.Count > 0
                ? string.Join(", ", profile.PreferredModelIds)
                : "(use current manual model or fallback gpt-5.5)";

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