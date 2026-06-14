using System;

namespace test
{
    /// <summary>
    /// File Generation v1 的產物：一個實際寫到磁碟的檔案（目前支援 markdown / 純文字）。
    /// 作為 workspace artifact 暴露，final answer 可引用，使用者可在 Workspace 看到並開啟。
    /// </summary>
    public sealed class GeneratedFilePayload
    {
        // markdown / text（之後擴充 docx / pdf / pptx）
        public string Format { get; init; } = "markdown";

        public string FileName { get; init; } = "";

        // 完整絕對路徑，供使用者開啟
        public string FilePath { get; init; } = "";

        public string Title { get; init; } = "";

        public int CharacterCount { get; init; }

        public int ByteCount { get; init; }

        public bool Success { get; init; }

        // 產物狀態：completed / failed（圖片任務的即時 queued / generating 由 orchestration 階段反映）。
        public string Status { get; init; } = "completed";

        public string ErrorMessage { get; init; } = "";

        // 來源說明：例如「research_first / 3 筆 verified_facts」
        public string SourceSummary { get; init; } = "";

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }
}
