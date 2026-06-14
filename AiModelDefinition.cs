using System.Collections.Generic;

namespace test
{
    public sealed class AiModelDefinition
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string IconPath { get; init; } = "";

        public AiProviderType Provider { get; init; } = AiProviderType.Unknown;
        public AiModelCapability Capabilities { get; init; } = AiModelCapability.None;

        // Multi-Model v1：成本層級（UI 顯示 + 依能力/成本的 fallback 排序）。
        public AiCostTier CostTier { get; init; } = AiCostTier.Standard;

        // Multi-Model v1：擴充點是否已啟用。休眠中的 provider（如 Gemini）設 false，
        // 不會出現在模型選單，也不會被 fallback / capability 重導選到。
        public bool IsAvailable { get; init; } = true;

        // 給節點保存使用的正式 model id
        public bool IsDefaultNodeModel { get; init; }

        // 給實際 API 呼叫使用的 model 名稱
        // 例如 pplx-sonar -> sonar
        public string ServiceModel { get; init; } = "";

        public bool IsDeepResearch { get; init; }

        public bool SupportsStreaming => Capabilities.HasFlag(AiModelCapability.Streaming);
        public bool SupportsImages => Capabilities.HasFlag(AiModelCapability.Images);
        public bool SupportsFiles => Capabilities.HasFlag(AiModelCapability.Files);
        public bool SupportsSearch => Capabilities.HasFlag(AiModelCapability.Search);
        public bool SupportsLongContext => Capabilities.HasFlag(AiModelCapability.LongContext);
        public bool SupportsImageGeneration => Capabilities.HasFlag(AiModelCapability.ImageGeneration);
        public bool SupportsVideoGeneration => Capabilities.HasFlag(AiModelCapability.VideoGeneration);
        public bool SupportsCode => Capabilities.HasFlag(AiModelCapability.Code);

        public string CostTierLabel => AiCostTierText.ToLabel(CostTier);

        /// <summary>UI 用的能力標籤（含成本層級），例如「搜尋・視覺・長文・標準」。</summary>
        public IReadOnlyList<string> CapabilityTags
        {
            get
            {
                var tags = new List<string>();

                if (SupportsSearch) tags.Add("搜尋");
                if (SupportsImages) tags.Add("視覺");
                if (SupportsImageGeneration) tags.Add("生成圖片");
                if (SupportsVideoGeneration) tags.Add("生成影片");
                if (SupportsCode) tags.Add("程式碼");
                if (SupportsFiles) tags.Add("檔案");
                if (SupportsLongContext) tags.Add("長文");

                tags.Add(CostTierLabel);
                return tags;
            }
        }

        /// <summary>給 ComboBox tooltip 用的單行能力摘要。</summary>
        public string CapabilitySummary => string.Join("・", CapabilityTags);
    }
}
