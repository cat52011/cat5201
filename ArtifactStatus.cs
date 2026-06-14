using System;

namespace test
{
    /// <summary>
    /// Workspace / Artifact v2：標準化的 artifact 狀態。
    /// draft → 草稿（未完成）、ready → 就緒、validated → 已驗證、exported → 已匯出（落地成檔）、failed → 失敗。
    /// </summary>
    public static class ArtifactStatus
    {
        public const string Draft = "draft";
        public const string Ready = "ready";
        public const string Validated = "validated";
        public const string Exported = "exported";
        public const string Failed = "failed";

        public static string Normalize(string? status)
        {
            string s = (status ?? "").Trim().ToLowerInvariant();
            return s switch
            {
                Draft => Draft,
                Ready => Ready,
                Validated => Validated,
                Exported => Exported,
                Failed => Failed,
                "ok" => Ready,
                "success" => Ready,
                "passed" => Validated,
                "valid" => Validated,
                "error" => Failed,
                "fail" => Failed,
                _ => string.IsNullOrWhiteSpace(s) ? Ready : s
            };
        }

        public static string ToLabel(string? status)
        {
            return Normalize(status) switch
            {
                Draft => "草稿",
                Ready => "就緒",
                Validated => "已驗證",
                Exported => "已匯出",
                Failed => "失敗",
                _ => "就緒"
            };
        }

        /// <summary>狀態徽章顏色 (背景, 文字)。</summary>
        public static (string Background, string Foreground) Colors(string? status)
        {
            return Normalize(status) switch
            {
                Draft => ("#F2F4F7", "#667085"),
                Ready => ("#EAF1FD", "#2E6FE0"),
                Validated => ("#E7F6EC", "#1F8A4C"),
                Exported => ("#EAF7F3", "#0E8A6E"),
                Failed => ("#FCEBEA", "#C0392B"),
                _ => ("#EAF1FD", "#2E6FE0")
            };
        }
    }
}
