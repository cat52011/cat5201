using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace test
{
    /// <summary>
    /// Video Gen v1：建立「Claude 導演 prompt」並把 Claude 回傳的 JSON 解析成 VideoPlanPayload。
    /// 純文字處理，不直接呼叫模型（呼叫由 AgentRuntime 透過既有 executor 完成），方便測試與重用。
    /// 解析失敗時退回最小可用計畫（把整段文字當 logline / video prompt），確保影片任務不會因解析失敗而中斷。
    /// </summary>
    /// <summary>
    /// 影片剪接節奏：第一層判定影片任務後，再細分要哪一種。
    /// Continuous＝連貫長鏡頭（Veo 原生延伸，同一鏡頭一路延長，省、人物連續，但慢、無快切）。
    /// FastCut＝快剪 / 預告片（獨立生成多個短鏡頭 + ffmpeg 裁切拼接，能做快節奏，但鏡頭間會跳、較貴、需 ffmpeg）。
    /// </summary>
    public enum VideoCutMode
    {
        Continuous,
        FastCut
    }

    public static class VideoPlanBuilder
    {
        // Veo：基底 8 秒，之後每段延伸 +7 秒，最多延伸 20 次（總長上限 148 秒）。
        public const int BaseSegmentSeconds = 8;
        public const int ExtendSegmentSeconds = 7;
        public const int MaxSegments = 21; // 1 基底 + 20 延伸

        // 快剪：每個鏡頭以 Veo base 最短 4 秒生成，剪輯時裁成短鏡頭；上限 6 鏡頭避免成本爆炸。
        // 成本：Veo 3.1 = $0.40 USD/秒（720p）；4 秒/鏡頭 = $1.60/鏡頭；6 鏡頭 = ~$9.6 USD（約 A$15）。
        // 連貫延伸：8 秒基底 + 7 秒延伸 = 15 秒 = $6 USD（約 A$9）——連貫比快剪省錢很多。
        public const int FastCutGenerateSeconds = 4;
        public const int MaxFastCutShots = 6; // 原 16 太貴；6 鏡頭快剪 ≈ A$15，已接近合理上限

        /// <summary>
        /// 影片任務的第二層細分：判斷要走「快剪」還是「連貫長鏡頭」。
        /// 預告片 / 快剪 / 混剪 / MV / 蒙太奇等明顯快節奏訊號 → FastCut；其餘（敘事、情緒、紀錄）→ Continuous。
        /// </summary>
        public static VideoCutMode DetectCutMode(string? userInput)
        {
            string s = (userInput ?? "").ToLowerInvariant();
            if (s.Length == 0) return VideoCutMode.Continuous;

            string[] fastCutSignals =
            {
                "預告片", "預告", "快剪", "快切", "混剪", "剪輯", "蒙太奇",
                "快節奏", "節奏感", "mv", "mtv", "music video",
                "trailer", "montage", "fast cut", "fast-cut", "quick cut", "teaser"
            };

            foreach (var sig in fastCutSignals)
                if (s.Contains(sig, StringComparison.Ordinal))
                    return VideoCutMode.FastCut;

            return VideoCutMode.Continuous;
        }

        /// <summary>
        /// 快剪鏡頭數：每鏡頭在成品中約顯示 2.5 秒，依目標秒數換算，夾在 [2, 16]。
        /// </summary>
        public static int FastCutShotCount(int targetSeconds)
        {
            int t = targetSeconds > 0 ? targetSeconds : BaseSegmentSeconds;
            int shots = (int)Math.Round(t / 2.5);
            if (shots < 2) shots = 2;
            return shots > MaxFastCutShots ? MaxFastCutShots : shots;
        }

        /// <summary>
        /// 從使用者文字偵測想要的影片秒數：支援「N 秒 / N 秒鐘 / N sec / N second(s)」「N 分 / N 分鐘 / N min」。
        /// 找不到回預設 8 秒；上限 148 秒（Veo 延伸總長上限）。
        /// </summary>
        public static int ParseRequestedSeconds(string? text)
        {
            string s = (text ?? "");
            if (s.Length == 0) return BaseSegmentSeconds;

            // 分鐘（先抓，避免「分」被當秒）
            var min = System.Text.RegularExpressions.Regex.Match(s,
                @"(\d+(?:\.\d+)?)\s*(?:分鐘|分|minutes?|mins?|m\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (min.Success && double.TryParse(min.Groups[1].Value, out var mv) && mv > 0)
                return Clamp((int)System.Math.Round(mv * 60));

            var sec = System.Text.RegularExpressions.Regex.Match(s,
                @"(\d+(?:\.\d+)?)\s*(?:秒鐘|秒|seconds?|secs?|s\b)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            if (sec.Success && double.TryParse(sec.Groups[1].Value, out var sv) && sv > 0)
                return Clamp((int)System.Math.Round(sv));

            return BaseSegmentSeconds;

            static int Clamp(int v)
            {
                int max = BaseSegmentSeconds + ExtendSegmentSeconds * (MaxSegments - 1); // 148
                if (v < 1) return 1;
                return v > max ? max : v;
            }
        }

        /// <summary>
        /// 單段基底秒數量化：Veo base 只接受 4 / 6 / 8 秒（離散值，其他會被 API 400 拒）。
        /// 把任意目標秒數對到最近的合法值，避免送出 5 / 7 等非法值。
        /// </summary>
        public static int QuantizeBaseSeconds(int targetSeconds)
        {
            if (targetSeconds <= 4) return 4;
            if (targetSeconds <= 5) return 4; // 5 偏 4
            if (targetSeconds <= 7) return 6; // 6、7 偏 6
            return 8;
        }

        /// <summary>依目標秒數換算需要幾段（每段 = 一個連續鏡頭）。≤8 秒 = 1 段。</summary>
        public static int SegmentsForDuration(int targetSeconds)
        {
            if (targetSeconds <= BaseSegmentSeconds)
                return 1;
            int extra = (targetSeconds - BaseSegmentSeconds + ExtendSegmentSeconds - 1) / ExtendSegmentSeconds; // ceil
            int segments = 1 + extra;
            return segments < 1 ? 1 : (segments > MaxSegments ? MaxSegments : segments);
        }

        public static string BuildDirectorPrompt(
            string userInput, int targetSeconds, string stylePrompt,
            string? treatment = null, VideoCutMode cutMode = VideoCutMode.Continuous)
        {
            string topic = (userInput ?? "").Trim();
            int seconds = targetSeconds > 0 ? targetSeconds : BaseSegmentSeconds;
            int segments = cutMode == VideoCutMode.FastCut
                ? FastCutShotCount(seconds)
                : SegmentsForDuration(seconds);
            string style = string.IsNullOrWhiteSpace(stylePrompt) ? VideoStyle.DefaultCinematicPrompt : stylePrompt.Trim();

            // 主合成（general-agent 已寫好的企劃 / treatment）若有，當作導演的改編底本，
            // 讓「實際採用的影片計畫」與使用者看到的主合成一致、且建立在更完整的敘事上。
            string treatmentBlock = string.IsNullOrWhiteSpace(treatment)
                ? ""
                : $@"

【上游已產出的企劃（請以此為改編底本，不要另起爐灶）】
下面是團隊已經寫好的影片企劃。請**忠實沿用它的片名、核心概念、敘事順序與情緒**，只把它「重新編排成下方 JSON 的可拍計畫」，並遷就影片模型的現實限制（連續長鏡頭、無法渲染文字、段數有限）。不要改掉它的故事，只是把它落地成能實際生成的鏡頭。
────────
{treatment.Trim()}
────────";

            string continuityRule;
            if (cutMode == VideoCutMode.FastCut)
            {
                // 快剪：每個 scene 是一個獨立的短鏡頭（會各自獨立生成、再剪短拼接成快節奏）。
                continuityRule =
$@"- 這是一支「快剪 / 預告片」節奏的影片，會用 {segments} 個**獨立短鏡頭**剪接而成，所以請正好產出 {segments} 個 scene。
- 每個 scene 是一個**獨立、自成一格的衝擊性鏡頭**（不同景別 / 角度 / 瞬間），在成品裡只佔約 2–3 秒，要一眼抓住注意力。鏡頭之間是「硬切」，不需要動作連貫，但要靠剪輯節奏堆疊張力。
- **為了視覺一致**：所有鏡頭請維持同一主角外型、同一世界觀與色調（在每個 segment_prompt 重述主角外型、服裝、配色、環境關鍵特徵），即使是硬切也要像同一部片。
- 把敘事能量分配成「開場鉤子 → 逐步升溫 → 最強高潮」的順序，最後一個 scene 是最有衝擊力的收尾畫面（關鍵意象 / 定格 / 淡出），讓 {seconds} 秒像一支完整預告片。
- 每個 scene 的動作要單純清楚（單一主體、一個明確動作），快剪鏡頭短，畫面越乾淨越不容易出現破綻。";
            }
            else if (segments > 1)
            {
                continuityRule =
$@"- 這支影片會用「影片延伸」技術接成 {segments} 段（基底 8 秒 + 每段延伸 7 秒），所以請正好產出 {segments} 個 scene。
- 每個 scene 是一段約 8 秒的「連續鏡頭」。**後一個 scene 必須從前一個 scene 的最後畫面自然延續**（同一場景或順剪、同一主角、動作連貫），像同一顆鏡頭的延續，不要硬切到不相關的畫面。
- 把完整的敘事弧（開場 → 鋪陳 → 高潮）分配到這 {segments} 段裡，讓 {seconds} 秒講完一個完整段落，而不是只擷取片段。
- 為了讓延伸保持人物 / 場景一致，請在每個 scene 的 segment_prompt 重述主角外型與場景關鍵特徵（服裝、髮型、配色、環境）。";
            }
            else
            {
                continuityRule = "- 只產出 1 個 scene（影片約 8 秒，單一連續鏡頭），並在這 8 秒內講完一個有頭有尾的完整片段。";
            }

            return
$@"你是頂尖的電影廣告導演與分鏡師，擅長高級、有質感、有情緒的電影感影像。請依使用者需求，產出一支約 {seconds} 秒短影片的完整製作計畫。

【使用者需求（原始，可能只是一句話）】
{topic}{treatmentBlock}

【第一步：把需求改寫成精細劇本（最重要）】
使用者的輸入往往只是粗略一句話。請先像專業導演一樣，把它**展開、深化成具體可拍的劇本概念**，不要只是覆述：
- 設定明確的主角（外型、年齡、服裝、神情）、場景（地點、時間、天氣、光線來源）、情緒基調與敘事弧。
- 想像具體的動作、細節與感官元素（光影、材質、聲音暗示、微表情、環境互動），讓畫面有層次與故事性。
- 若需求抽象，主動補上一個有電影感、能打動人的具體詮釋；若需求具體，則強化其質感與情緒。
- logline 要凝練但有畫面感；style_definition 在下方風格基礎上補上這支片專屬的攝影細節。
之後所有 scene、segment_prompt、video_prompt 都要建立在這個精細劇本之上。

【必須遵守的視覺風格（原廠 / 使用者指定，所有鏡頭一律套用，疊加在上面的劇本之上）】
{style}

【影片模型的硬限制（務必遵守，否則成品會出現明顯破綻）】
- **絕對不要在畫面裡放任何文字**：片名字卡、字幕、Logo、報紙標題、刻字、招牌等都不要寫進 segment_prompt。影片模型無法渲染清晰文字，一定會變成扭曲亂碼。需要片名時改用「畫面意象」收尾（如：徽記、戒指、火光、淡出黑場），片名留給後製。
- **每個鏡頭聚焦單一清楚主體**：避免一個鏡頭塞很多小小的人/群眾/同時多個主角近距離激烈互動，這類最容易出現變形、多手多臉、肢體黏連等破綻。動作要明確、單純、可信。
- 避免過快過亂的剪輯式動作（影片模型擅長連貫運鏡，不擅長一鏡內多次硬切）。
- **最後一個 scene 必須是收尾鏡頭**：把情緒推到高潮後給一個明確的結束畫面（定格、緩緩淡出、關鍵意象收束），讓 {seconds} 秒看起來是「剛好講完」而不是被硬生生切斷。

請只輸出一個 JSON 物件（不要加 markdown 圍欄、不要多餘文字），結構如下：
{{
  ""title"": ""影片標題"",
  ""logline"": ""一句話核心概念"",
  ""style_definition"": ""整體視覺風格、色調、質感、參考風格（必須延續上面指定的風格，用英文寫，給影片模型用）"",
  ""music_brief"": ""配樂方向：曲風、節奏、情緒"",
  ""total_duration_seconds"": {seconds},
  ""scenes"": [
    {{
      ""narration"": ""這個鏡頭的旁白（口語、可直接配音）"",
      ""visual"": ""畫面內容描述"",
      ""camera"": ""鏡頭設計：景別 / 運鏡 / 構圖"",
      ""keyframe_prompt"": ""關鍵畫面的英文 image prompt（給 Flux / Midjourney）"",
      ""segment_prompt"": ""這一段約 8 秒鏡頭給影片模型的英文 prompt：描述畫面、動作、運鏡，並在開頭融入上面的視覺風格關鍵字。不要含任何文字/字卡。"",
      ""duration_seconds"": 8
    }}
  ],
  ""video_prompt"": ""整合風格與所有鏡頭、給影片模型的一段英文 prompt（涵蓋整體風格與主線）""
}}

要求：
{continuityRule}
- 旁白用使用者的語言；keyframe_prompt、segment_prompt、video_prompt、style_definition 一律用英文（影片 / 影像模型對英文 prompt 表現較好）。
- segment_prompt 要寫得豐富具體（主角、動作、運鏡、光影、環境細節），不是一句帶過；並在開頭帶入電影感風格關鍵字（filmic、35mm、shallow depth of field、soft light、film grain 等），確保整支影片風格一致、不過度銳利。
- segment_prompt 結尾可加上負面提示，例如：no text, no captions, no subtitles, no watermark, no logo, no distorted faces, no extra limbs, no morphing。
- visual / narration 也要具體、有情緒，可直接拍攝與配音，不要空泛或公式化。";
        }

        public static VideoPlanPayload Parse(string? modelText, string userInput, int targetSeconds)
        {
            var fallback = BuildFallbackPlan(userInput, targetSeconds, modelText);

            string json = ExtractJsonObject(modelText);
            if (string.IsNullOrWhiteSpace(json))
                return fallback;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                var plan = new VideoPlanPayload
                {
                    Title = GetString(root, "title"),
                    Logline = GetString(root, "logline"),
                    StyleDefinition = GetString(root, "style_definition"),
                    MusicBrief = GetString(root, "music_brief"),
                    TotalDurationSeconds = GetInt(root, "total_duration_seconds", targetSeconds > 0 ? targetSeconds : 8),
                    VideoPromptForGenerator = GetString(root, "video_prompt")
                };

                var scenes = new List<VideoScenePayload>();
                if (root.TryGetProperty("scenes", out var scenesEl) && scenesEl.ValueKind == JsonValueKind.Array)
                {
                    int i = 1;
                    foreach (var s in scenesEl.EnumerateArray())
                    {
                        scenes.Add(new VideoScenePayload
                        {
                            Index = i++,
                            Narration = GetString(s, "narration"),
                            Visual = GetString(s, "visual"),
                            Camera = GetString(s, "camera"),
                            KeyframePrompt = GetString(s, "keyframe_prompt"),
                            SegmentVideoPrompt = GetString(s, "segment_prompt"),
                            DurationSeconds = GetInt(s, "duration_seconds", 0)
                        });
                    }
                }

                plan.Scenes = scenes;

                if (string.IsNullOrWhiteSpace(plan.VideoPromptForGenerator))
                    plan.VideoPromptForGenerator = ComposeVideoPrompt(plan, userInput);

                if (string.IsNullOrWhiteSpace(plan.Title))
                    plan.Title = fallback.Title;

                return plan;
            }
            catch
            {
                return fallback;
            }
        }

        private static VideoPlanPayload BuildFallbackPlan(string? userInput, int targetSeconds, string? modelText)
        {
            string topic = (userInput ?? "").Trim();
            string logline = string.IsNullOrWhiteSpace(modelText) ? topic : modelText.Trim();

            return new VideoPlanPayload
            {
                Title = topic.Length <= 40 ? topic : topic.Substring(0, 40),
                Logline = logline.Length <= 200 ? logline : logline.Substring(0, 200),
                StyleDefinition = "",
                MusicBrief = "",
                TotalDurationSeconds = targetSeconds > 0 ? targetSeconds : 8,
                Scenes = Array.Empty<VideoScenePayload>(),
                VideoPromptForGenerator = string.IsNullOrWhiteSpace(topic) ? logline : topic
            };
        }

        private static string ComposeVideoPrompt(VideoPlanPayload plan, string userInput)
        {
            var sb = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(plan.StyleDefinition))
                sb.Append("Style: ").Append(plan.StyleDefinition.Trim()).Append(". ");

            foreach (var scene in plan.Scenes ?? Array.Empty<VideoScenePayload>())
            {
                if (!string.IsNullOrWhiteSpace(scene.Visual))
                    sb.Append(scene.Visual.Trim()).Append(' ');
                if (!string.IsNullOrWhiteSpace(scene.Camera))
                    sb.Append('(').Append(scene.Camera.Trim()).Append("). ");
            }

            string composed = sb.ToString().Trim();
            return string.IsNullOrWhiteSpace(composed) ? (userInput ?? "").Trim() : composed;
        }

        // 從模型回傳中抓出第一個完整 JSON 物件（容忍 ```json 圍欄 / 前後雜訊）。
        private static string ExtractJsonObject(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            int start = text.IndexOf('{');
            int end = text.LastIndexOf('}');
            if (start < 0 || end <= start)
                return "";

            return text.Substring(start, end - start + 1);
        }

        private static string GetString(JsonElement el, string name)
        {
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty(name, out var v) &&
                v.ValueKind == JsonValueKind.String)
            {
                return (v.GetString() ?? "").Trim();
            }
            return "";
        }

        private static int GetInt(JsonElement el, string name, int fallback)
        {
            if (el.ValueKind == JsonValueKind.Object && el.TryGetProperty(name, out var v))
            {
                if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n))
                    return n;
                if (v.ValueKind == JsonValueKind.String && int.TryParse(v.GetString(), out var s))
                    return s;
            }
            return fallback;
        }
    }
}
