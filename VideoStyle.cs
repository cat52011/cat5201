namespace test
{
    /// <summary>
    /// 影片導演的「原廠預設風格」。這是注入給 Claude 導演 + Veo 的視覺風格基底。
    ///
    /// 個人化頁面會完整顯示這段預設 prompt，讓使用者照同樣格式寫自己的風格；
    /// 使用者未自訂時一律用這個預設。
    ///
    /// 設計重點（2026-06-20 更新，基於 yzavoku / Voku Studio / Donkey Skin / The Color of Pomegranates 參考分析）：
    ///   - 核心定義：任何主題都以「劣化 16mm 膠片 + 象徵主義在場感」的語言呈現——視覺語言固定，內容跟使用者需求走；
    ///   - 影像質感：柔和顆粒膠片 + 霧感朦朧擴散（細顆粒 / 柔焦 / 光暈 / 霧氣），不要泛黃老舊感；
    ///   - 色調：中性銀灰 + 柔和低飽和 + 極淡暖意，不泛黃不老舊、非高飽和高對比；
    ///   - 構圖：象徵主義油畫式，靜止、有重量、在場感 > 動作感；
    ///   - 刻意避免：好萊塢光效、AI 泛類超現實（漂浮神殿/金雲）、數位完美感、商業廣告節奏。
    /// </summary>
    public static class VideoStyle
    {
        /// <summary>原廠預設風格（英文，影片/影像模型對英文表現最好）。</summary>
        public const string DefaultCinematicPrompt =
@"Visual aesthetic: a scene remembered through badly deteriorating archival film. Not because the subject is sacred — but because the image treats everything with weight, stillness, and silence. Any subject rendered in this language becomes a found fragment.

Film stock and deterioration (essential — this is what separates the look from generic AI): badly preserved 16mm analog footage, overexposed highlights with blooming halation, lens contamination, dust particles and scratches on emulsion, film gate weave, soft imperfect focus especially at frame edges, faded and damaged pigments. No clean digital sharpness anywhere in the frame. The image looks like evidence, not production.

Color and light: soft fine-grain film, gentle and atmospheric rather than aged or yellowed. Muted, desaturated palette — pale silvery neutrals, soft greys, cool dusty tones with only the faintest hint of warmth; restrained colour, low contrast. No clean blacks; only soft, hazy, deteriorated shadows. Lighting feels remembered rather than observed: gauzy, veiled, diffused, misty. Gentle blooming and soft halation around bright sources. Diffused soft backlight through thick atmospheric haze and fine mist.

Composition and movement: symbolist and contemplative — figures still, minimal action, maximum presence, time suspended. Deliberate, weighted, painterly framing. Wide and stable over fast and reactive.

Emotion: sacred melancholy, reverence, wonder, longing, silence. The atmosphere of something important happening that nobody needs to explain.

NOT: Hollywood lighting or action, high contrast, CGI perfection, clean digital look, generic AI surrealism (floating islands, golden clouds, cloaked figures), epic fantasy aesthetics, game artwork, oversaturation, fast cuts, polished commercial surfaces, modern fashion photography.";

        /// <summary>個人化頁面顯示用的一句話說明。</summary>
        public const string DefaultStyleSummary =
            "原廠預設：柔和顆粒膠片質感、霧感朦朧擴散光、中性銀灰柔和色調（不泛黃不老舊）、象徵主義構圖、靜默的在場感。任何主題都以此語言呈現。";

        /// <summary>
        /// 給影片模型（Veo）用的「精簡視覺標籤」。
        ///
        /// 為什麼要跟 DefaultCinematicPrompt 分開：完整版那段是寫給「導演（Claude）」看懂美學用的，
        /// 充滿抽象藝術評論與 NOT 清單。但影片模型 prompt 服從度很短，只認得具體視覺名詞——
        /// 把 350 字藝評＋否定清單原封塞給 Veo 只會稀釋訊號、甚至讓 NOT 裡的詞被當正面 token 渲染出來。
        /// 因此送進 Veo 的固定是這段「短、具體、純正面描述」的標籤（無抽象情緒、無 NOT 清單）。
        /// </summary>
        public const string DefaultVeoRenderTags =
            "Soft hazy atmosphere veiled in fine mist, dreamy ethereal diffusion, gauzy soft focus blooming gently at the edges. Soft visible film grain across the whole frame, delicate organic grain texture, subtle halation around soft highlights. Diffused soft light, muted and desaturated, pale silvery-neutral tones with only the faintest hint of warmth, soft clean whites. Low contrast, soft and grainy, misty foggy dreamlike atmospheric depth. Still, slow, contemplative.";

        /// <summary>
        /// 解析「實際生效」的風格：使用者自訂（去空白後非空且不等於預設）則用自訂，否則用原廠預設。
        /// </summary>
        public static string Resolve(string? userOverride)
        {
            string s = (userOverride ?? "").Trim();
            return s.Length == 0 ? DefaultCinematicPrompt : s;
        }

        /// <summary>
        /// 取「實際送進影片模型（Veo）」的風格標籤。
        /// 生效風格＝原廠預設時 → 用精簡標籤 DefaultVeoRenderTags（不把整段藝評丟給 Veo）；
        /// 使用者自訂時 → 直接用使用者那段（他自己決定要寫什麼給模型）。
        /// </summary>
        public static string RenderTagsFor(string? effectiveStyle)
        {
            string s = (effectiveStyle ?? "").Trim();
            if (s.Length == 0 ||
                string.Equals(s, DefaultCinematicPrompt.Trim(), System.StringComparison.Ordinal))
            {
                return DefaultVeoRenderTags;
            }
            return s;
        }
    }
}
