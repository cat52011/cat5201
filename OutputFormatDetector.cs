using System;

namespace test
{
    /// <summary>
    /// §6 多輸出偵測：集中判斷一段使用者輸入「想要哪些輸出檔」。
    /// OrchestrationPlanner（決定任務型別 / 加哪些 stage）與 AgentRuntime（實際產檔）共用同一套判斷，
    /// 避免兩邊關鍵字各寫一份而走樣。
    /// </summary>
    public static class OutputFormatDetector
    {
        private static readonly string[] PresentationKeywords =
        {
            "簡報", "投影片", "做成ppt", "做成 ppt", "生成ppt", "生成 ppt",
            "ppt", "pptx", "slides", "slide deck", "presentation", "deck"
        };

        private static readonly string[] WrittenReportKeywords =
        {
            "書面報告", "書面", "報告", "文件", "文檔", "文稿", "word", "docx",
            "doc 檔", "doc檔", "written report", "document"
        };

        private static readonly string[] SpreadsheetKeywords =
        {
            "excel", "xlsx", "試算表", "spreadsheet", "工作表", "表格檔", "csv 表", "報表",
            "表格", "比較表", "列表", "table"
        };

        public static bool WantsPresentation(string? userInput)
            => ContainsAny(userInput, PresentationKeywords);

        public static bool WantsWrittenReport(string? userInput)
            => ContainsAny(userInput, WrittenReportKeywords);

        public static bool WantsSpreadsheet(string? userInput)
            => ContainsAny(userInput, SpreadsheetKeywords);

        /// <summary>同時要求簡報「且」書面報告（例：「包括簡報跟書面報告」）。</summary>
        public static bool WantsBothDeckAndReport(string? userInput)
            => WantsPresentation(userInput) && WantsWrittenReport(userInput);

        private static bool ContainsAny(string? text, string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = text.ToLowerInvariant();
            foreach (var kw in keywords)
            {
                if (normalized.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
