using System;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    /// <summary>
    /// §6 第一層輸出判斷：先跑一次（便宜的）API，判斷使用者想要的輸出是「簡報 / 報告 / 表格」之中的哪幾個，
    /// 就像 AiAutoModelResolverService 判斷該給哪個模型一樣。API 失敗 / 無金鑰時退回關鍵字判斷，永不中斷。
    /// </summary>
    public sealed class OutputIntentResolver
    {
        private readonly AiServiceRouter _router;

        public OutputIntentResolver(AiServiceRouter router)
        {
            _router = router;
        }

        public async Task<OutputIntent> ResolveAsync(string topText, CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return OutputIntent.None;

            string raw;
            try
            {
                raw = await _router
                    .GetOpenAiService("gpt-5.5")
                    .GenerateAsync(BuildSystemPrompt(), BuildUserPrompt(topText), 200, ct);
            }
            catch
            {
                // API 失敗 → 退回關鍵字判斷，至少不漏掉明顯的輸出需求。
                return OutputIntent.FromKeywords(topText);
            }

            var apiIntent = TryParse(raw);
            if (apiIntent == null)
                return OutputIntent.FromKeywords(topText);

            // API 說「都不要檔案」，但關鍵字明顯指出要某種輸出時，以關鍵字為準，避免明明要卻沒產出。
            if (!apiIntent.WantsAny)
            {
                var kw = OutputIntent.FromKeywords(topText);
                if (kw.WantsAny)
                    return kw;
            }

            return apiIntent;
        }

        private static string BuildSystemPrompt()
        {
            return
@"你是一個「輸出格式判斷器」。根據使用者輸入，判斷他『要你實際產出的東西』是下列哪幾種（可多選，也可能完全不要）：

- presentation：簡報 / 投影片 / ppt / slides
- report：書面報告 / 文件 / 文章 / 說明文（Word 類）
- table：表格 / 試算表 / excel / 數據比較表
- image：圖片 / 圖像 / 插圖 / 畫一張（要你生成或修改一張圖）
- video：影片 / 短片 / 動畫 / 一段 X 秒的片子（要你生成一段影片）

你只能輸出 JSON，不要任何多餘文字、不要 markdown：

{
  ""presentation"": true/false,
  ""report"": true/false,
  ""table"": true/false,
  ""image"": true/false,
  ""video"": true/false
}

★最重要的原則：區分「使用者要你做出什麼」與「使用者只是在描述內容」。
只有使用者『下令產出』某種東西時才標 true。輸入內文裡提到的格式名稱，如果只是被當作『主題的一部分』『系統功能的介紹』『在描述別人做的東西』『要寫進簡報的條列內容』，一律不算，不要標 true。

判斷步驟：
1. 找出使用者的主要指令動詞（做一份 / 製作 / 生成 / 給我一個 / 畫 / 寫一份…）以及它的受詞是什麼。
2. 那個受詞才決定要產什麼。內文後面的描述、條列、舉例一律不影響判斷。

範例：
- 「做一份簡報，內容介紹這個能生成 PDF / Word / PPT / Excel 的系統」
  → 只有 presentation=true。Word/Excel/PPT 只是被介紹的功能，不是要產的檔。
- 「做一份報告，另外也幫我做一份簡報」
  → report=true 且 presentation=true（兩個都是明確指令）。
- 「整理成 excel 表格」 → table=true。
- 「給我一個大約15秒的影片，內容包含一隊人在沙漠中前進」
  → video=true。『給我一個…影片』是明確下令產出影片，後面是影片內容描述。
- 「畫一張賽博龐克城市的插圖」 → image=true。
- 「他昨天發了一個影片限動，超好笑」 → 全部 false。只是在描述別人發的影片，不是要你生影片。
- 「這張圖裡有幾隻貓？」 → 全部 false。是在問既有圖片的問題，不是要你產圖。
- 「幫我分析這份財報」（沒說要任何輸出）→ 全部 false。

其他原則：
- 使用者明確下令要簡報 / 報告 / 表格 / 圖片 / 影片 → 對應項 true。
- 只是問問題、聊天、描述、要一段純文字回答 → 全部 false。
- 不確定某項到底是『要產的東西』還是『內文描述』時，當作內文描述，標 false（寧可少產，不要多產）。
- 影片成本最高，特別謹慎：只有非常明確要你『生成一段影片』時才標 video=true。";
        }

        private static string BuildUserPrompt(string text)
        {
            return
$@"使用者輸入：
{text}

請輸出 JSON。";
        }

        private static OutputIntent? TryParse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            try
            {
                var match = Regex.Match(raw, @"\{[\s\S]*\}");
                string json = match.Success ? match.Value : raw;

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                return new OutputIntent
                {
                    WantsPresentation = GetBool(root, "presentation"),
                    WantsReport = GetBool(root, "report"),
                    WantsTable = GetBool(root, "table"),
                    WantsImage = GetBool(root, "image"),
                    WantsVideo = GetBool(root, "video"),
                    Source = "api:" + raw.Trim()
                };
            }
            catch
            {
                return null;
            }
        }

        private static bool GetBool(JsonElement root, string name)
        {
            if (!root.TryGetProperty(name, out var v))
                return false;

            return v.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String => bool.TryParse(v.GetString(), out var b) && b,
                _ => false
            };
        }
    }
}
