namespace test
{
    public enum AiProviderType
    {
        OpenAI = 0,
        Claude = 1,
        Perplexity = 2,
        Gemini = 3,        // Multi-Model v1：擴充點，休眠中（IsAvailable=false，給金鑰後啟用）
        Unknown = 99
    }
}