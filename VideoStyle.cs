namespace test
{
    /// <summary>
    /// 影片導演的「原廠預設風格」。這是注入給 Claude 導演 + Veo 的視覺風格基底。
    ///
    /// 個人化頁面會完整顯示這段預設 prompt，讓使用者照同樣格式寫自己的風格；
    /// 使用者未自訂時一律用這個預設（電影感、不過度銳利、高級質感）。
    ///
    /// 設計重點（對應使用者需求「成品不要太清晰但要很有電影風格」）：
    ///   - 明確要求 filmic / 底片質感、淺景深、柔和高光、輕微顆粒與 halation；
    ///   - 明確「避免」臨床式數位銳利、過飽和、塑膠感 CGI 平滑；
    ///   - 強調高級、氛圍、情緒共鳴的 arthouse 電影感。
    /// </summary>
    public static class VideoStyle
    {
        /// <summary>原廠預設電影風格（英文，影片/影像模型對英文表現最好）。</summary>
        public const string DefaultCinematicPrompt =
@"Cinematic film aesthetic, shot on 35mm film with an anamorphic lens. Shallow depth of field, soft natural lighting with gentle falloff and motivated practical sources. Rich filmic color grading: slightly muted, desaturated tones with warm, gently rolled-off highlights and deep but soft shadows. Subtle organic film grain, mild halation and bloom around highlights, natural lens breathing and gentle motion blur. The image is intentionally soft and textured — NOT clinically sharp, NOT oversaturated, NOT plastic or over-rendered CGI. Composition follows classic cinematography: deliberate framing, considered negative space, slow and intentional camera movement. Premium, atmospheric, emotionally resonant — like a frame from an arthouse feature film. Photochemical, tactile, and timeless rather than crisp digital video.";

        /// <summary>個人化頁面顯示用的一句話說明。</summary>
        public const string DefaultStyleSummary =
            "原廠預設：35mm 底片電影感、淺景深、柔和光線、輕微顆粒，刻意不過度銳利。";

        /// <summary>
        /// 解析「實際生效」的風格：使用者自訂（去空白後非空且不等於預設）則用自訂，否則用原廠預設。
        /// </summary>
        public static string Resolve(string? userOverride)
        {
            string s = (userOverride ?? "").Trim();
            return s.Length == 0 ? DefaultCinematicPrompt : s;
        }
    }
}
