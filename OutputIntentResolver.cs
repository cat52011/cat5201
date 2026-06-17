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
                    .GetOpenAiService("gpt-5.4")
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
@"你是一個「輸出格式判斷器」。根據使用者輸入，判斷他想要的『產出檔案』是下列哪幾種（可多選，也可能完全不要檔案）：

- presentation：簡報 / 投影片 / ppt / slides
- report：書面報告 / 文件 / 文章 / 說明文（Word 類）
- table：表格 / 試算表 / excel / 數據比較表

你只能輸出 JSON，不要任何多餘文字、不要 markdown：

{
  ""presentation"": true/false,
  ""report"": true/false,
  ""table"": true/false
}

判斷原則：
- 使用者明說要簡報 → presentation=true。
- 使用者明說要報告 / 文件 / 寫一篇 → report=true。
- 使用者要表格 / 比較表 / excel / 把數據列出來 → table=true。
- 同時要多種就都標 true（例：「報告加簡報」→ presentation 與 report 都 true）。
- 只是問問題、聊天、要一段純文字回答、不需要產生檔案 → 三個都 false。
- 不確定時保守判斷，寧可 false。";
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
