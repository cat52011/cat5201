namespace test
{
    /// <summary>
    /// Multi-Model v1：模型成本層級。用於 UI 顯示與「依能力＋成本」的 fallback 排序。
    /// Economy → 便宜（適合 Auto / 大量呼叫）、Standard → 一般、Premium → 高階（Auto 模式預設避免）。
    /// </summary>
    public enum AiCostTier
    {
        Economy = 0,
        Standard = 1,
        Premium = 2
    }

    public static class AiCostTierText
    {
        public static string ToLabel(AiCostTier tier) => tier switch
        {
            AiCostTier.Economy => "經濟",
            AiCostTier.Standard => "標準",
            AiCostTier.Premium => "高階",
            _ => "標準"
        };

        /// <summary>徽章顏色 (背景, 文字)。</summary>
        public static (string Background, string Foreground) Colors(AiCostTier tier) => tier switch
        {
            AiCostTier.Economy => ("#E7F6EC", "#1F8A4C"),
            AiCostTier.Standard => ("#EAF1FD", "#2E6FE0"),
            AiCostTier.Premium => ("#FBEFE6", "#B45309"),
            _ => ("#EAF1FD", "#2E6FE0")
        };
    }
}
