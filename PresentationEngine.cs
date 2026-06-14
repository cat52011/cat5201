namespace test
{
    /// <summary>
    /// 簡報生成器（個人化設定可切換）：決定「撰寫簡報內容」交給哪個 AI。
    /// 流程固定：重寫需求 → 併入 Perplexity 查到的研究素材 → 交生成器撰寫；差別只在生成器是誰。
    /// </summary>
    public enum PresentationEngine
    {
        /// <summary>Claude（預設）：Claude Sonnet 4.6 依研究素材撰寫結構化簡報內容。</summary>
        Claude = 0,

        /// <summary>GPT：改用 OpenAI 模型撰寫簡報內容。</summary>
        Gpt = 1,

        /// <summary>Gamma：Claude 產內容 + Gamma 排版出高品質 .pptx。需 GAMMA_API_KEY，九月開放，目前停用。</summary>
        Gamma = 2
    }

    public static class PresentationEngineHelper
    {
        public static PresentationEngine Parse(string? value)
        {
            return (value ?? "").Trim().ToLowerInvariant() switch
            {
                "gpt" or "openai" or "1" => PresentationEngine.Gpt,
                "gamma" or "2" => PresentationEngine.Gamma,
                _ => PresentationEngine.Claude
            };
        }

        public static string ToStorageValue(PresentationEngine engine)
        {
            return engine switch
            {
                PresentationEngine.Gpt => "Gpt",
                PresentationEngine.Gamma => "Gamma",
                _ => "Claude"
            };
        }

        /// <summary>選定的生成器 → 「撰寫簡報內容」用的模型 id。Gamma 仍由 Claude 產內容（Gamma 只做排版）。</summary>
        public static string ToAuthorModelId(PresentationEngine engine)
        {
            return engine switch
            {
                PresentationEngine.Gpt => AiModels.OpenAi_Gpt54,
                _ => AiModels.Claude_Sonnet46
            };
        }

        /// <summary>顯示用簡短名稱（loading 提示 / 決策窗）。</summary>
        public static string ToDisplayName(PresentationEngine engine)
        {
            return engine switch
            {
                PresentationEngine.Gpt => "GPT",
                PresentationEngine.Gamma => "Gamma",
                _ => "Claude"
            };
        }
    }
}
