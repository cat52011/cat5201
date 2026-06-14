namespace test
{
    /// <summary>
    /// Video Gen v1 狀態：影片任務的生命週期。
    /// Queued / Generating 是執行中的即時狀態（由 generate_video 階段 + 節點 loading 提示反映，
    /// 影片是長時間非同步任務，需輪詢）；Completed / Failed / Canceled 是最終結果，
    /// 記錄在 VideoRequestPayload.Status 與 GeneratedFilePayload.Status 上。
    /// </summary>
    public enum VideoGenerationStatus
    {
        Queued,
        Generating,
        Completed,
        Failed,
        Canceled
    }

    public static class VideoGenerationStatusText
    {
        public static string ToStorageValue(VideoGenerationStatus status) => status switch
        {
            VideoGenerationStatus.Queued => "queued",
            VideoGenerationStatus.Generating => "generating",
            VideoGenerationStatus.Completed => "completed",
            VideoGenerationStatus.Failed => "failed",
            VideoGenerationStatus.Canceled => "canceled",
            _ => "queued"
        };

        public static string ToLabel(VideoGenerationStatus status) => status switch
        {
            VideoGenerationStatus.Queued => "排隊中",
            VideoGenerationStatus.Generating => "生成中",
            VideoGenerationStatus.Completed => "已完成",
            VideoGenerationStatus.Failed => "失敗",
            VideoGenerationStatus.Canceled => "已取消",
            _ => "排隊中"
        };
    }
}
