using System;

namespace test
{
    public static class AiAutoCostPolicy
    {
        // §15 個人化成本控制：使用者可關閉這兩個高成本模型。
        // 與 Auto 的內建降級不同，這兩個開關「無論手動或自動」都會把對應模型降級，做為成本保險。
        // 由 MainWindow 在載入 / 切換設定時設定；靜態狀態，單一視窗下安全。
        public static bool BlockOpus { get; set; }
        public static bool BlockDeepResearch { get; set; }

        /// <summary>若使用者已關閉該高成本模型，回傳降級後模型並輸出 true；否則原樣回傳。</summary>
        public static bool TryEnforceUserBlock(string? modelId, out string resolvedModel)
        {
            string normalized = AiModelHelper.NormalizeNodeModel(modelId);

            if (BlockOpus &&
                string.Equals(normalized, AiModels.Claude_Opus46, StringComparison.OrdinalIgnoreCase))
            {
                resolvedModel = AiModels.Claude_Sonnet46;
                return true;
            }

            if (BlockDeepResearch &&
                string.Equals(normalized, AiModels.Perplexity_SonarDeepResearch, StringComparison.OrdinalIgnoreCase))
            {
                resolvedModel = AiModels.Perplexity_Sonar;
                return true;
            }

            resolvedModel = normalized;
            return false;
        }
    }
}
