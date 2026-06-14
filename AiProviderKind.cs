namespace test
{
    public enum AiProviderKind
    {
        OpenAI = 0,
        Claude = 1,
        PerplexitySonar = 2,
        PerplexityTool = 3,
        Gemini = 4,        // Multi-Model v1：擴充點，休眠中（尚未實作 GeminiProvider）
        Unknown = 99
    }
}