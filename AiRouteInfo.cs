namespace test
{
    public sealed class AiRouteInfo
    {
        public string NodeModel { get; init; } = "";
        public AiProviderKind Provider { get; init; } = AiProviderKind.Unknown;

        // 實際傳給 service / API 的 model 名稱
        // 例如：
        // NodeModel = "pplx-sonar"
        // ServiceModel = "sonar"
        public string ServiceModel { get; init; } = "";

        public bool IsDeepResearch { get; init; }

        public bool IsValid =>
            !string.IsNullOrWhiteSpace(NodeModel) &&
            Provider != AiProviderKind.Unknown &&
            !string.IsNullOrWhiteSpace(ServiceModel);
    }
}