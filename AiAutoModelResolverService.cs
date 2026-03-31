using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class AiAutoModelResolution
    {
        public string RecommendedModel { get; init; } = "";
        public NodeTaskMode TaskMode { get; init; } = NodeTaskMode.Chat;
        public double Confidence { get; init; }

        // 預留 debug 用
        public string RawResponse { get; init; } = "";
    }

    public sealed class AiAutoModelResolverService
    {
        private readonly AiServiceRouter _router;

        public AiAutoModelResolverService(AiServiceRouter router)
        {
            _router = router;
        }

        public async Task<AiAutoModelResolution> ResolveAsync(
            string topText,
            IReadOnlyList<MainWindow.AttachmentInfo> attachments,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return CreateFallback("empty input");

            string systemPrompt = BuildSystemPrompt();
            string userPrompt = BuildUserPrompt(topText, attachments);

            string raw;

            try
            {
                raw = await _router
                    .GetOpenAiService("gpt-5.4")
                    .GenerateAsync(systemPrompt, userPrompt, 300, ct);
            }
            catch
            {
                return CreateFallback("api error");
            }

            var parsed = TryParse(raw);

            if (parsed != null)
            {
                return new AiAutoModelResolution
                {
                    RecommendedModel = parsed.RecommendedModel,
                    TaskMode = parsed.TaskMode,
                    Confidence = parsed.Confidence,
                    RawResponse = raw
                };
            }

            return CreateFallback("parse failed", raw);
        }

        private static string BuildSystemPrompt()
        {
            return
@"你是一個 AI 模型路由器（AI Router）。

你的任務是：
根據使用者輸入內容，判斷最適合的模型與任務類型。

你只能輸出 JSON，不要輸出任何額外文字。

格式如下：

{
  ""task_mode"": ""Chat | Research | Translate | Summarize | Rewrite | Extract | Code"",
  ""recommended_model"": ""gpt-5.4 | claude-sonnet-4-6 | claude-opus-4-6 | pplx-sonar | pplx-sonar-deep-research"",
  ""confidence"": 0.0
}

判斷規則：

【Research】
- 查資料 / 最新資訊 / 比較 / 分析
→ pplx-sonar

【Deep Research】
- 很長內容 / 多來源 / 深度分析
→ pplx-sonar-deep-research

【Translate】
- 翻譯 / 菜單 / PDF / 文件
→ gpt-5.4

【Code】
- 程式 / 架構 / debug
→ claude-opus-4-6

【Rewrite / Summarize】
- 改寫 / 潤稿 / 精簡
→ claude-sonnet-4-6

【Chat】
- 一般對話
→ gpt-5.4

重要規則：
- 只輸出 JSON
- 不要解釋
- 不要加 markdown
";
        }

        private static string BuildUserPrompt(
            string text,
            IReadOnlyList<MainWindow.AttachmentInfo> attachments)
        {
            return
$@"使用者輸入：
{text}

附件數量：{attachments?.Count ?? 0}

請輸出 JSON。";
        }

        private static AiAutoModelResolution? TryParse(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return null;

            try
            {
                string json = ExtractJson(raw);

                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement;

                string model = root.TryGetProperty("recommended_model", out var m)
                    ? m.GetString() ?? ""
                    : "";

                string mode = root.TryGetProperty("task_mode", out var tm)
                    ? tm.GetString() ?? ""
                    : "";

                double confidence = root.TryGetProperty("confidence", out var c)
                    && c.TryGetDouble(out var val)
                    ? val
                    : 0.5;

                model = AiModelHelper.NormalizeNodeModel(model);
                var taskMode = NodeTaskModeHelper.ParseOrDefault(mode);

                if (string.IsNullOrWhiteSpace(model))
                    return null;

                return new AiAutoModelResolution
                {
                    RecommendedModel = model,
                    TaskMode = taskMode,
                    Confidence = confidence,
                    RawResponse = raw
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ExtractJson(string text)
        {
            var match = Regex.Match(text, @"\{[\s\S]*\}");
            return match.Success ? match.Value : text;
        }

        private static AiAutoModelResolution CreateFallback(
            string reason,
            string raw = "")
        {
            return new AiAutoModelResolution
            {
                RecommendedModel = AiModelRegistry.Default.Id,
                TaskMode = NodeTaskMode.Chat,
                Confidence = 0.3,
                RawResponse = $"[fallback: {reason}] {raw}"
            };
        }
    }
}