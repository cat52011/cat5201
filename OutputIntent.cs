namespace test
{
    /// <summary>
    /// §6 第一層輸出判斷的結果：使用者這次想要「簡報 / 報告 / 表格」之中的哪幾個（可多選、可全無）。
    /// 由 OutputIntentResolver（先跑一次 API 判斷）產生，找不到時退回關鍵字判斷。
    /// 實際產檔規則（AgentRuntime）：報告→.docx、表格→.xlsx、簡報→.pptx，且「不管哪一種都一律再配一份 .pdf」。
    /// </summary>
    public sealed class OutputIntent
    {
        public bool WantsPresentation { get; init; }
        public bool WantsReport { get; init; }
        public bool WantsTable { get; init; }

        // 影片 / 圖片也納入同一個第一層 LLM 判斷（與簡報/報告/表格同一真相來源），
        // 不再只靠 OrchestrationPlanner 的關鍵字白名單——避免「給我一個15秒的影片」這種講法漏掉。
        public bool WantsVideo { get; init; }
        public bool WantsImage { get; init; }

        // debug：API 原始回覆 / 判斷來源
        public string Source { get; init; } = "";

        public bool WantsAny => WantsPresentation || WantsReport || WantsTable || WantsVideo || WantsImage;

        /// <summary>白話摘要，給決策窗「執行摘要」顯示這次第一層判斷出的想要輸出。</summary>
        public string ToSummary()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (WantsReport) parts.Add("報告");
            if (WantsTable) parts.Add("表格");
            if (WantsPresentation) parts.Add("簡報");
            if (WantsImage) parts.Add("圖片");
            if (WantsVideo) parts.Add("影片");
            return parts.Count == 0 ? "純文字回答" : string.Join("、", parts);
        }

        public static OutputIntent None => new OutputIntent { Source = "none" };

        /// <summary>關鍵字後援判斷（API 失敗或未啟用時用）。影片/圖片沿用 OrchestrationPlanner 的關鍵字。</summary>
        public static OutputIntent FromKeywords(string? text)
        {
            var taskType = OrchestrationPlanner.ResolveTaskType(text, NodeTaskMode.Chat);
            return new OutputIntent
            {
                WantsPresentation = OutputFormatDetector.WantsPresentation(text),
                WantsReport = OutputFormatDetector.WantsWrittenReport(text),
                WantsTable = OutputFormatDetector.WantsSpreadsheet(text),
                WantsVideo = taskType == OrchestrationTaskType.VideoGeneration,
                WantsImage = taskType == OrchestrationTaskType.ImageGeneration,
                Source = "keywords"
            };
        }
    }
}
