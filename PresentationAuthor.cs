using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace test
{
    /// <summary>
    /// Presentation v1.5（Gamma / NotebookLM 之前的「夠用」占位）：兩段式作者流程
    /// ——先（可選）用 Perplexity 取得即時事實，再請選定的生成器（Claude / GPT）一次撰寫結構化 JSON deck。
    ///
    /// 刻意保持簡單：PptxBuilder / Marp 只是過渡輸出，內容品質的真正投資留到接上 Gamma / NotebookLM 時再做，
    /// 不在這個會被取代的占位層上做逐頁深寫等重工。任一步失敗回 null，呼叫端 fallback 回
    /// <see cref="PresentationOutlineBuilder"/> 的確定性切段（demo 不開天窗）。
    ///
    /// 本類別只負責組 prompt 與把 JSON 解析成 <see cref="PresentationOutlinePayload"/>；模型呼叫由 AgentRuntime
    /// 透過既有 executor 完成。
    /// </summary>
    public static class PresentationAuthor
    {
        // 內容投影片（不含封面 / 來源頁）的硬上限，避免模型暴衝。
        private const int MaxContentSlides = 20;

        /// <summary>請 Perplexity 針對主題蒐集「可放進簡報」的即時、具體事實。</summary>
        public static string BuildResearchPrompt(string userInput, int requestedSlides)
        {
            string topic = (userInput ?? "").Trim();
            int slides = requestedSlides > 0 ? requestedSlides : 6;

            return
$@"我要做一份關於以下主題的簡報（約 {slides} 頁），請幫我蒐集最新、最關鍵的事實與數據作為素材。

【主題】
{topic}

請條列輸出（每點一行），聚焦：
- 具體數字、日期、金額、市佔、規格、財報等可查證的事實（標明年份 / 時間點）。
- 重要事件、轉折、現況與趨勢。
- 若有對立觀點或風險，也列出。
盡量精簡、每點一個事實，附上來源名稱。不要寫成文章，給我可直接引用的要點。";
        }

        /// <summary>請選定的生成器依研究素材一次設計出結構化 JSON 簡報。</summary>
        public static string BuildAuthorPrompt(
            string userInput, string? researchText, string? existingDraft, int requestedSlides)
        {
            string topic = (userInput ?? "").Trim();

            string slideCountRule = requestedSlides > 0
                ? $"請產出剛好 {requestedSlides} 張內容投影片（不含封面與資料來源頁）。"
                : "請依內容自行決定張數，通常 5 到 8 張內容投影片。";

            string research = string.IsNullOrWhiteSpace(researchText)
                ? "（沒有額外研究素材，請用你自己的可靠知識，並標明屬於概述。）"
                : researchText.Trim();

            string draft = string.IsNullOrWhiteSpace(existingDraft)
                ? ""
                : "\n\n【既有草稿（可參考、可改寫，不必照抄）】\n" + existingDraft.Trim();

            return
$@"你是資深簡報顧問與內容設計師。請依【需求】與【研究素材】，設計一份專業、可直接上台的簡報。

【需求】
{topic}

【研究素材】
{research}{draft}

只輸出一個 JSON 物件（不要 markdown 圍欄、不要多餘文字），結構如下：
{{
  ""title"": ""簡報標題（精煉、有主題性，不要是指令）"",
  ""subtitle"": ""一句副標 / 主旨"",
  ""slides"": [
    {{
      ""heading"": ""這張投影片的標題（短、具體）"",
      ""bullets"": [""重點一（具體、含數據或洞見）"", ""重點二"", ""重點三""]
    }}
  ],
  ""sources"": [""來源名稱或網址"", ""...""]
}}

要求：
- {slideCountRule}
- 每張投影片 3～5 個重點；每個重點是一句精煉、具體、可上台講的話，不要整段文章、不要空泛口號。
- 內容要有邏輯流（背景 → 核心 → 數據 / 案例 → 風險 / 結論），盡量引用研究素材中的具體數字與事實。
- 用與【需求】相同的語言撰寫。
- 若研究素材含來源，挑 3～8 個放進 sources；沒有就給空陣列。";
        }

        /// <summary>
        /// 把作者模型回傳的 JSON 解析成 PresentationOutlinePayload。
        /// 解析失敗或沒有任何有效投影片時回 null，讓呼叫端 fallback。
        /// </summary>
        public static PresentationOutlinePayload? Parse(
            string? modelText,
            string userInput,
            int requestedSlides,
            string pipelineId,
            string modelId,
            string agentId)
        {
            string json = ExtractJsonObject(modelText);
            if (string.IsNullOrWhiteSpace(json))
                return null;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string title = GetString(root, "title");
                string subtitle = GetString(root, "subtitle");

                if (string.IsNullOrWhiteSpace(title))
                    title = OneLine(userInput, 40);

                var slides = new List<PresentationSlidePayload>();
                int order = 1;

                // 封面
                slides.Add(new PresentationSlidePayload
                {
                    Order = order++,
                    Kind = "cover",
                    Heading = string.IsNullOrWhiteSpace(title) ? "簡報" : title,
                    Bullets = Array.Empty<string>()
                });

                // 內容投影片
                if (root.TryGetProperty("slides", out var slidesEl) && slidesEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var s in slidesEl.EnumerateArray())
                    {
                        if (slides.Count(x => x.Kind == "content") >= MaxContentSlides)
                            break;

                        string heading = GetString(s, "heading");
                        var bullets = GetStringArray(s, "bullets");

                        if (string.IsNullOrWhiteSpace(heading) && bullets.Count == 0)
                            continue;

                        slides.Add(new PresentationSlidePayload
                        {
                            Order = order++,
                            Kind = "content",
                            Heading = string.IsNullOrWhiteSpace(heading) ? "重點" : heading,
                            Bullets = bullets
                        });
                    }
                }

                // 沒有任何內容投影片 → 視為失敗，讓呼叫端 fallback。
                if (slides.Count(x => x.Kind == "content") == 0)
                    return null;

                // 資料來源頁
                var sources = GetStringArray(root, "sources");
                if (sources.Count > 0)
                {
                    slides.Add(new PresentationSlidePayload
                    {
                        Order = order++,
                        Kind = "sources",
                        Heading = "資料來源",
                        Bullets = sources.Take(8).ToList()
                    });
                }

                return new PresentationOutlinePayload
                {
                    Title = title,
                    Topic = string.IsNullOrWhiteSpace(subtitle) ? OneLine(userInput, 60) : subtitle,
                    Slides = slides,
                    SlideCount = slides.Count,
                    RequestedSlideCount = requestedSlides,
                    PipelineId = pipelineId ?? "",
                    ModelId = modelId ?? "",
                    AgentId = agentId ?? "",
                    SourceSummary = sources.Count > 0 ? $"作者模型撰寫 / {sources.Count} 筆來源" : "作者模型撰寫"
                };
            }
            catch
            {
                return null;
            }
        }

        // ---- 解析輔助 ----

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

        private static List<string> GetStringArray(JsonElement el, string name)
        {
            var list = new List<string>();
            if (el.ValueKind == JsonValueKind.Object &&
                el.TryGetProperty(name, out var arr) &&
                arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in arr.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String)
                    {
                        string s = (item.GetString() ?? "").Trim();
                        if (!string.IsNullOrWhiteSpace(s))
                            list.Add(s);
                    }
                }
            }
            return list;
        }

        private static string OneLine(string? text, int max)
        {
            string s = (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length > max)
                s = s.Substring(0, max).TrimEnd() + "…";
            return s;
        }
    }
}
