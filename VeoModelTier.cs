namespace test
{
    /// <summary>
    /// Veo 3.1 三個品質 / 成本檔位（個人化可選）。前期測試預設 Lite（最省）。
    /// Standard 最貴最好；Fast 折衷；Lite 最便宜，適合測試 / 草稿。
    /// </summary>
    public enum VeoModelTier
    {
        Standard,
        Fast,
        Lite
    }

    /// <summary>
    /// Veo 三檔的單一真相來源：tier → model id / 顯示名 / 每秒美金單價 / 能力。
    /// 模型選擇與成本估算都讀這裡，避免散落。
    ///
    /// model id 已對官方文件校正（ai.google.dev/gemini-api/docs/video，2026-06）。
    /// 若日後 API 回 404，可改下方常數一處，或用環境變數 CAT5201_VEO_MODEL 整支覆寫（免重編）。
    /// </summary>
    public static class VeoModels
    {
        public const string StandardModel = "veo-3.1-generate-preview";
        public const string FastModel = "veo-3.1-fast-generate-preview";
        public const string LiteModel = "veo-3.1-lite-generate-preview";

        // 每秒美金（720p）。Veo 3.1：標準 $0.40、Fast $0.15、Lite ~$0.03–0.05（取 0.05 含音）。
        public const double StandardUsdPerSecond = 0.40;
        public const double FastUsdPerSecond = 0.15;
        public const double LiteUsdPerSecond = 0.05;

        /// <summary>
        /// 是否支援「影片延伸（extend）」。Veo 3.1 Lite 官方明示不支援延伸與 4k——
        /// 因此 Lite 無法用連貫多段做長片，連貫任務只能單段 8 秒（要長片請切 Fast/標準，或用快剪獨立鏡頭）。
        /// </summary>
        public static bool SupportsExtend(VeoModelTier tier) => tier != VeoModelTier.Lite;

        public static VeoModelTier ParseTier(string? s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                case "standard":
                case "標準":
                case "標準版":
                    return VeoModelTier.Standard;
                case "fast":
                    return VeoModelTier.Fast;
                default:
                    return VeoModelTier.Lite; // 預設 Lite（前期測試省錢）
            }
        }

        public static string ToStorageValue(VeoModelTier tier) => tier switch
        {
            VeoModelTier.Standard => "Standard",
            VeoModelTier.Fast => "Fast",
            _ => "Lite"
        };

        public static string ModelId(VeoModelTier tier) => tier switch
        {
            VeoModelTier.Standard => StandardModel,
            VeoModelTier.Fast => FastModel,
            _ => LiteModel
        };

        public static double UsdPerSecond(VeoModelTier tier) => tier switch
        {
            VeoModelTier.Standard => StandardUsdPerSecond,
            VeoModelTier.Fast => FastUsdPerSecond,
            _ => LiteUsdPerSecond
        };

        public static string DisplayName(VeoModelTier tier) => tier switch
        {
            VeoModelTier.Standard => "Veo 3.1 標準版",
            VeoModelTier.Fast => "Veo 3.1 Fast",
            _ => "Veo 3.1 Lite"
        };
    }
}
