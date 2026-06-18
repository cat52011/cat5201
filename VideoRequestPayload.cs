using System;

namespace test
{
    /// <summary>
    /// Video Gen v1：「prompt → 影片請求」的 workspace artifact（ItemType = "video_request"）。
    /// 記錄非同步影片任務的 job id、狀態、進度與最終落地檔案，讓 Workspace 可追蹤、可顯示連結。
    /// </summary>
    public sealed class VideoRequestPayload
    {
        public string JobId { get; set; } = "";

        public string Prompt { get; init; } = "";

        public string Model { get; set; } = "";

        public int DurationSeconds { get; set; }

        public string Size { get; set; } = "";

        // queued / generating / completed / failed / canceled
        public string Status { get; set; } = "queued";

        public int ProgressPercent { get; set; }

        public int PollCount { get; set; }

        // 完成後可開啟 / 匯出的 mp4 路徑；失敗 / 未啟用時為空。
        public string FilePath { get; set; } = "";

        public string ErrorMessage { get; set; } = "";

        public DateTime CreatedAtUtc { get; init; } = DateTime.UtcNow;

        public bool Success => string.Equals(Status, "completed", StringComparison.OrdinalIgnoreCase);
    }
}
