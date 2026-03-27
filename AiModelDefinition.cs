namespace test
{
    public sealed class AiModelDefinition
    {
        public string Id { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string IconPath { get; init; } = "";

        public AiProviderType Provider { get; init; } = AiProviderType.Unknown;
        public AiModelCapability Capabilities { get; init; } = AiModelCapability.None;

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
    }
}