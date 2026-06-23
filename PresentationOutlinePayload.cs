using System;
using System.Collections.Generic;

namespace test
{
    /// <summary>
    /// Presentation Agent v1 的結構化產物：把內容拆解成「投影片大綱 / slide plan」。
    /// 純結構（標題 + 每張投影片的標題與重點），不含檔案；實際 .md deck 由 GeneratedFileWriter 另外寫出。
    /// 作為 workspace artifact 暴露，讓使用者在 Workspace 看到簡報的骨架，也供之後 PPTX 產生器重用。
    /// </summary>
    public sealed class PresentationOutlinePayload
    {
        // 簡報標題（封面用）
        public string Title { get; init; } = "";

        // 副標 / 主題說明
        public string Topic { get; init; } = "";

        public IReadOnlyList<PresentationSlidePayload> Slides { get; init; }
            = Array.Empty<PresentationSlidePayload>();

        public int SlideCount { get; init; }

        // 使用者要求的張數（0 = 未指定，由內容自動決定）
        public int RequestedSlideCount { get; init; }

        public string PipelineId { get; init; } = "";

        public string ModelId { get; init; } = "";

        public string AgentId { get; init; } = "";

        // 來源說明：例如「research_first / 3 筆 verified_facts」
        public string SourceSummary { get; init; } = "";

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;
    }

    public sealed class PresentationSlidePayload
    {
        // 1 = 封面，之後依序遞增
        public int Order { get; init; }

        // cover / content / sources
        public string Kind { get; init; } = "content";

        public string Heading { get; init; } = "";

        public IReadOnlyList<string> Bullets { get; init; } = Array.Empty<string>();

        // 智慧配圖：本頁的配圖（執行期填入，由 builder 嵌入 pptx / deck.pdf）。
        // [JsonIgnore]：byte[] 不進 workspace artifact 的 JSON 序列化，避免 base64 撐爆存檔。
        [System.Text.Json.Serialization.JsonIgnore]
        public byte[]? ImageBytes { get; set; }
    }
}
