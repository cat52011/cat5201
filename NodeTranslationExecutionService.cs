using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class NodeTranslationExecutionService
    {
        private sealed class SegmentPlanItem
        {
            public string Title { get; set; } = "";
            public string Hint { get; set; } = "";
        }

        private readonly AiServiceRouter _router;
        private readonly Func<NodeControl, string, string, NodeTaskMode, CancellationToken, Task<string>> _singlePassAsync;
        private readonly Func<NodeControl, string, string, NodeTaskMode, Action<string>, CancellationToken, Task<string>> _singlePassStreamAsync;
        private readonly Func<NodeControl, string, string, string, NodeTaskMode, bool, int, CancellationToken, Task<AiRequest>> _buildAiRequestAsync;
        private readonly NodeInstructionBuilder _instructionBuilder;
        private readonly NodeTextProcessingService _textProcessing;

        private readonly int _segmentDiscoveryMaxTokens;
        private readonly int _segmentTranslationMaxTokens;

        public NodeTranslationExecutionService(
    AiServiceRouter router,
    NodeInstructionBuilder instructionBuilder,
    NodeTextProcessingService textProcessing,
    Func<NodeControl, string, string, NodeTaskMode, CancellationToken, Task<string>> singlePassAsync,
    Func<NodeControl, string, string, NodeTaskMode, Action<string>, CancellationToken, Task<string>> singlePassStreamAsync,
    Func<NodeControl, string, string, string, NodeTaskMode, bool, int, CancellationToken, Task<AiRequest>> buildAiRequestAsync,
    int segmentDiscoveryMaxTokens,
    int segmentTranslationMaxTokens)
        {
            _router = router;
            _instructionBuilder = instructionBuilder;
            _textProcessing = textProcessing;
            _singlePassAsync = singlePassAsync;
            _singlePassStreamAsync = singlePassStreamAsync;
            _buildAiRequestAsync = buildAiRequestAsync;
            _segmentDiscoveryMaxTokens = segmentDiscoveryMaxTokens;
            _segmentTranslationMaxTokens = segmentTranslationMaxTokens;
        }

        public async Task<string> TranslateAsync(
            NodeControl currentNode,
            string topText,
            string model,
            NodeTaskMode taskMode,
            CancellationToken ct)
        {
            var segments = await TryDiscoverSegmentsAsync(currentNode, topText, model, ct);
            if (segments.Count <= 1)
                return await _singlePassAsync(currentNode, topText, model, taskMode, ct);

            var sb = new StringBuilder();

            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var seg = segments[i];
                int index = i + 1;

                string segmentPrompt =
$@"【系統判定任務模式】
{taskMode}

使用者要求：
{topText}

請只處理附件中的第 {index}/{segments.Count} 段：
標題：{seg.Title}
提示：{seg.Hint}

要求：
1. 只翻譯這一段，不要翻其它段。
2. 依照原文件內容完整翻譯，不要省略。
3. 若原文已有分類或菜名結構，請保留清楚排版。
4. 不要寫前言、不要寫處理流程、不要寫「以下為」。
5. 若你發現這段內容和前面段落高度重複，請只輸出這一段真正新增的內容，不要重複整份文件。
6. 這一段完成後，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";

                string instructions = _instructionBuilder.BuildSegmentTranslationInstructions();

                var request = await _buildAiRequestAsync(
                    currentNode,
                    model,
                    instructions,
                    segmentPrompt,
                    taskMode,
                    false,
                    _segmentTranslationMaxTokens,
                    ct);

                var provider = _router.GetProvider(model);
                var response = await provider.GenerateAsync(request, ct);
                string translated = _textProcessing.RemoveRepeatedBlocks((response.Text ?? "").Trim());

                if (string.IsNullOrWhiteSpace(translated) || _textProcessing.SegmentLooksDuplicate(sb, translated))
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(translated);
            }

            var final = _textProcessing.RemoveRepeatedBlocks(sb.ToString().Trim());
            if (string.IsNullOrWhiteSpace(final))
                return await _singlePassAsync(currentNode, topText, model, taskMode, ct);

            return final;
        }

        public async Task<string> TranslateStreamAsync(
            NodeControl currentNode,
            string topText,
            string model,
            NodeTaskMode taskMode,
            Action<string> onDelta,
            CancellationToken ct)
        {
            var segments = await TryDiscoverSegmentsAsync(currentNode, topText, model, ct);
            if (segments.Count <= 1)
                return await _singlePassStreamAsync(currentNode, topText, model, taskMode, onDelta, ct);

            var sb = new StringBuilder();
            bool firstVisibleSegment = true;

            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var seg = segments[i];
                int index = i + 1;

                string segmentPrompt =
$@"【系統判定任務模式】
{taskMode}

使用者要求：
{topText}

請只處理附件中的第 {index}/{segments.Count} 段：
標題：{seg.Title}
提示：{seg.Hint}

要求：
1. 只翻譯這一段，不要翻其它段。
2. 依照原文件內容完整翻譯，不要省略。
3. 若原文已有分類或菜名結構，請保留清楚排版。
4. 不要寫前言、不要寫處理流程、不要寫「以下為」。
5. 若你發現這段內容和前面段落高度重複，請只輸出這一段真正新增的內容，不要重複整份文件。
6. 這一段完成後，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";

                string instructions = _instructionBuilder.BuildSegmentTranslationInstructions();

                var request = await _buildAiRequestAsync(
                    currentNode,
                    model,
                    instructions,
                    segmentPrompt,
                    taskMode,
                    true,
                    _segmentTranslationMaxTokens,
                    ct);

                var provider = _router.GetProvider(model);
                bool segmentStarted = false;

                var response = await provider.GenerateStreamAsync(
                    request,
                    delta =>
                    {
                        if (!segmentStarted)
                        {
                            segmentStarted = true;
                            if (!firstVisibleSegment)
                                onDelta?.Invoke(Environment.NewLine + Environment.NewLine);
                        }

                        onDelta?.Invoke(delta);
                    },
                    ct);

                string translated = _textProcessing.RemoveRepeatedBlocks((response.Text ?? "").Trim());

                if (string.IsNullOrWhiteSpace(translated) || _textProcessing.SegmentLooksDuplicate(sb, translated))
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(translated);
                firstVisibleSegment = false;
            }

            var final = _textProcessing.RemoveRepeatedBlocks(sb.ToString().Trim());
            if (string.IsNullOrWhiteSpace(final))
                return await _singlePassStreamAsync(currentNode, topText, model, taskMode, onDelta, ct);

            return final;
        }

        private async Task<List<SegmentPlanItem>> TryDiscoverSegmentsAsync(
            NodeControl currentNode,
            string topText,
            string model,
            CancellationToken ct)
        {
            var discoveryPrompt =
$@"請根據目前附件文件內容，將整份文件拆成「按原始順序」處理的邏輯段落。
適用於：菜單、PDF、文章、說明文件。

重要規則：
1. 只輸出 JSON。
2. JSON 格式必須是：
{{""segments"":[{{""title"":""..."",""hint"":""...""}}]}}
3. title 請用該段在原文件中的標題或最明顯辨識名稱。
4. hint 請用很短的描述，幫助辨識該段內容。
5. 至少拆成 2 段；若真的無法拆段，仍輸出 1 段。
6. 不要翻譯，不要摘要，不要解釋。

使用者需求：
{topText}";

            string instructions = _instructionBuilder.BuildSegmentDiscoveryInstructions();

            var request = await _buildAiRequestAsync(
                currentNode,
                model,
                instructions,
                discoveryPrompt,
                NodeTaskMode.Translate,
                false,
                _segmentDiscoveryMaxTokens,
                ct);

            var provider = _router.GetProvider(model);
            var response = await provider.GenerateAsync(request, ct);
            string raw = response.Text;

            if (string.IsNullOrWhiteSpace(raw))
                return new List<SegmentPlanItem>();

            try
            {
                var json = raw.Trim();
                int firstBrace = json.IndexOf('{');
                int lastBrace = json.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace > firstBrace)
                    json = json.Substring(firstBrace, lastBrace - firstBrace + 1);

                using var doc = JsonDocument.Parse(json);
                if (!doc.RootElement.TryGetProperty("segments", out var arr) || arr.ValueKind != JsonValueKind.Array)
                    return new List<SegmentPlanItem>();

                var result = new List<SegmentPlanItem>();
                foreach (var item in arr.EnumerateArray())
                {
                    string title = item.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "" : "";
                    string hint = item.TryGetProperty("hint", out var hintEl) ? hintEl.GetString() ?? "" : "";

                    title = title.Trim();
                    hint = hint.Trim();

                    if (!string.IsNullOrWhiteSpace(title))
                    {
                        result.Add(new SegmentPlanItem
                        {
                            Title = title,
                            Hint = hint
                        });
                    }
                }

                return result;
            }
            catch
            {
                return new List<SegmentPlanItem>();
            }
        }
    }
}