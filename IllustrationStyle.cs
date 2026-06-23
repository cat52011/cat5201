namespace test
{
    /// <summary>
    /// 簡報 / 報告「配圖」的統一視覺風格與提示詞組裝。
    ///
    /// 使用者選定風格＝「簡約現代 / 資訊示意」（2026-06-21）。設計目的：取代過去封面圖的死板模板
    /// （只塞標題 → 圖片模型生出無意義的抽象 clip-art），讓所有配圖一律走「乾淨、現代、扁平資訊示意」
    /// 風格，且內容與該頁 / 章節「呼應」（先由 Claude 萃取具體畫面，再套此風格）。
    ///
    /// 簡報封面、（後續）內容頁配圖、報告章節配圖都共用這支，確保整體調性一致。
    /// </summary>
    public static class IllustrationStyle
    {
        /// <summary>
        /// 送進圖片模型的風格後綴（英文，模型對英文表現好）。純正面描述、無 NOT 清單以外的否定堆疊，
        /// 末段的 no text / no watermark 是圖片模型實務上有效的硬性排除（避免亂碼文字、浮水印）。
        /// </summary>
        public const string StyleSuffix =
            "Clean modern flat infographic illustration, minimalist vector style, simple clear geometric shapes and symbols, " +
            "bright airy light background, fresh friendly modern multi-color palette, generous whitespace, soft subtle shadows, " +
            "polished business presentation aesthetic, one clear positive easy-to-read concept. " +
            "No text, no words, no letters, no numbers, no watermark, no logo.";

        /// <summary>給「圖片 brief 萃取」LLM 的風格指示（中文，讓 Claude 寫出符合此風格的具體畫面）。</summary>
        public const string BriefStyleGuidance =
            "風格固定為：乾淨、現代、扁平的『資訊示意圖 / 向量插畫』，用簡單清楚的幾何元素與象徵符號表達單一核心概念，" +
            "配色明亮清新專業、淺色背景、留白充足；畫面要正向、直覺、好理解，避免黑暗、威脅、詭異或過度隱喻的視覺；" +
            "不是寫實照片、不是繁複場景、畫面中不要出現任何文字。";

        /// <summary>把一段「具體畫面 brief」組裝成最終圖片提示（brief + 統一風格後綴）。brief 空白時退回純風格描述。</summary>
        public static string Compose(string? brief)
        {
            string b = (brief ?? "").Trim();
            return b.Length == 0 ? StyleSuffix : b + " " + StyleSuffix;
        }
    }
}
