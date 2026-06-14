namespace test
{
    /// <summary>
    /// Image Gen v1 狀態：圖片任務的生命週期。
    /// Queued / Generating 是執行中的即時狀態（由 OrchestrationStateMachine 的 generate_image 階段反映，
    /// 並由節點 loading 指示器顯示「正在生成圖片…」）；Completed / Failed 是最終結果，
    /// 記錄在 GeneratedFilePayload.Status 上。
    /// </summary>
    public enum ImageGenerationStatus
    {
        Queued,
        Generating,
        Completed,
        Failed
    }

    public static class ImageGenerationStatusText
    {
        public static string ToStorageValue(ImageGenerationStatus status) => status switch
        {
            ImageGenerationStatus.Queued => "queued",
            ImageGenerationStatus.Generating => "generating",
            ImageGenerationStatus.Completed => "completed",
            ImageGenerationStatus.Failed => "failed",
            _ => "completed"
        };
    }
}
