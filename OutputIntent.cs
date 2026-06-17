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

        // debug：API 原始回覆 / 判斷來源
        public string Source { get; init; } = "";

        public bool WantsAny => WantsPresentation || WantsReport || WantsTable;

        /// <summary>白話摘要，給決策窗「執行摘要」顯示這次第一層判斷出的想要輸出。</summary>
        public string ToSummary()
        {
            var parts = new System.Collections.Generic.List<string>();
            if (WantsReport) parts.Add("報告");
            if (WantsTable) parts.Add("表格");
            if (WantsPresentation) parts.Add("簡報");
            return parts.Count == 0 ? "純文字回答" : string.Join("、", parts);
        }

        public static OutputIntent None => new OutputIntent { Source = "none" };

        /// <summary>關鍵字後援判斷（API 失敗或未啟用時用）。</summary>
        public static OutputIntent FromKeywords(string? text)
        {
            return new OutputIntent
            {
                WantsPresentation = OutputFormatDetector.WantsPresentation(text),
                WantsReport = OutputFormatDetector.WantsWrittenReport(text),
                WantsTable = OutputFormatDetector.WantsSpreadsheet(text),
                Source = "keywords"
            };
        }
    }
}
