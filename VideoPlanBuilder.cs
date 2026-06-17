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
    public static class VideoPlanBuilder
    {
        public static string BuildDirectorPrompt(string userInput, int targetSeconds)
        {
            string topic = (userInput ?? "").Trim();
            int seconds = targetSeconds > 0 ? targetSeconds : 8;

            return
$@"你是專業的影片導演與分鏡師。請依使用者需求，產出一支約 {seconds} 秒短影片的完整製作計畫。

【使用者需求】
{topic}

請只輸出一個 JSON 物件（不要加 markdown 圍欄、不要多餘文字），結構如下：
{{
  ""title"": ""影片標題"",
  ""logline"": ""一句話核心概念"",
  ""style_definition"": ""整體視覺風格、色調、質感、參考風格（給關鍵畫面與影片模型用）"",
  ""music_brief"": ""配樂方向：曲風、節奏、情緒"",
  ""total_duration_seconds"": {seconds},
  ""scenes"": [
    {{
      ""narration"": ""這個鏡頭的旁白（口語、可直接配音）"",
      ""visual"": ""畫面內容描述"",
      ""camera"": ""鏡頭設計：景別 / 運鏡 / 構圖"",
      ""keyframe_prompt"": ""關鍵畫面的英文 image prompt（給 Flux / Midjourney）"",
      ""duration_seconds"": 4
    }}
  ],
  ""video_prompt"": ""整合風格與所有鏡頭、給 Veo 3 的一段英文 prompt""
}}

要求：
- 2 到 5 個 scene，duration 加總約等於 total_duration_seconds。
- 旁白用使用者的語言；keyframe_prompt 與 video_prompt 用英文（影片 / 影像模型對英文 prompt 表現較好）。
- 內容具體、可拍攝，不要空泛。";
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
