using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Controls;

namespace test
{
    public sealed class NodeService
    {
        private readonly AiServiceRouter _router;
        private readonly MainWindow _main;

        private const int MainReplyMaxOutputTokens = 8000;
        private const int ContinuationMaxRounds = 5;
        private const int SegmentDiscoveryMaxTokens = 1200;
        private const int SegmentTranslationMaxTokens = 8000;
        private const int OtherNodeContextLimit = 3;

        private sealed class SegmentPlanItem
        {
            public string Title { get; set; } = "";
            public string Hint { get; set; } = "";
        }

        private sealed class ConnectionInfo
        {
            public NodeControl? StartNode { get; set; }
            public string StartThumb { get; set; } = "ThumbTL";
            public NodeControl? EndNode { get; set; }
            public string EndThumb { get; set; } = "ThumbTR";
        }

        public NodeService(AiServiceRouter router, MainWindow main)
        {
            _router = router;
            _main = main;
        }

        private static string GetRuntimeModelLabel(string model)
        {
            model = AiModelHelper.NormalizeNodeModel(model);

            return model switch
            {
                "claude-sonnet-4-6" => "Claude Sonnet 4.6",
                "claude-opus-4-6" => "Claude Opus 4.6",
                "pplx-sonar" => "Perplexity Sonar",
                "pplx-sonar-deep-research" => "Perplexity Sonar Deep Research",
                _ => "OpenAI GPT-5.4"
            };
        }

        private static string BuildModelIdentityGuard(string model)
        {
            var runtimeLabel = GetRuntimeModelLabel(model);

            return
$@"

【模型身分規則】
你目前實際執行的模型是：{runtimeLabel}。
若使用者詢問你是什麼模型、你來自哪一家、或你是否為 OpenAI / Claude / Perplexity，
你必須依照上面這個實際模型名稱誠實回答。
不要把自己說成別的模型，不要把自己統稱為 OpenAI，也不要捏造未提供的型號。";
        }

        private static string BuildGeneralNodeInstructions(string model)
        {
            return
                "你是一個節點內容生成助手。" +
                "請直接完成目前節點上半部要求的內容，不要先寫任務流程、操作步驟、整理原則、校對流程、備份說明或前言。" +
                "除非使用者明確要求步驟說明，否則請直接輸出結果本身。" +
                "若是翻譯需求，就直接翻譯；若是整理需求，就直接整理完成內容；若是問答需求，就直接回答。" +
                "回應請使用繁體中文。" +
                "可以參考上下游節點，但不要被其它節點的語氣或格式帶偏。" +
                "若有附件（圖片/檔案），請閱讀後直接根據附件內容作答。" +
                BuildModelIdentityGuard(model) +
                "\n\n完整輸出完成後，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private static string BuildPerplexityInstructions(string model, bool isDeepResearch)
        {
            var baseText = isDeepResearch
                ? "你是一個研究型節點內容生成助手。請直接輸出整理完成後的內容本身，使用繁體中文。不要重述題目，不要輸出前言，不要輸出思考流程。"
                : "你是一個即時搜尋型節點內容生成助手。請直接輸出完成結果本身，使用繁體中文。不要重述題目，不要輸出前言，不要輸出思考流程。";

            return baseText + BuildModelIdentityGuard(model);
        }

        public async Task<string> GenerateAsync(NodeControl node, string topText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return "";

            var route = PrepareRoute(_main.GetNodeSelectedModel(node));
            string model = route.NodeModel;

            if (route.Provider == AiProviderKind.PerplexitySonar)
            {
                return await GenerateSinglePassOrContinuedAsync(node, topText, model, ct);
            }

            bool useSegmentMode =
                LooksLikeFullTranslationRequest(topText) &&
                HasNonImageAttachments(node);

            if (useSegmentMode)
            {
                try
                {
                    var segmented = await TranslateBySegmentsAsync(node, topText, model, ct);
                    if (!string.IsNullOrWhiteSpace(segmented))
                        return segmented;
                }
                catch
                {
                }
            }

            return await GenerateSinglePassOrContinuedAsync(node, topText, model, ct);
        }

        public async Task<string> GenerateStreamAsync(
    NodeControl node,
    string topText,
    Action<string> onDelta,
    CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return "";

            var route = PrepareRoute(_main.GetNodeSelectedModel(node));
            string model = route.NodeModel;

            if (route.Provider == AiProviderKind.PerplexitySonar)
            {
                return await GenerateSinglePassOrContinuedStreamAsync(node, topText, model, onDelta, ct);
            }

            bool useSegmentMode =
                LooksLikeFullTranslationRequest(topText) &&
                HasNonImageAttachments(node);

            if (useSegmentMode)
            {
                try
                {
                    var segmented = await TranslateBySegmentsStreamAsync(node, topText, model, onDelta, ct);
                    if (!string.IsNullOrWhiteSpace(segmented))
                        return segmented;
                }
                catch
                {
                }
            }

            return await GenerateSinglePassOrContinuedStreamAsync(node, topText, model, onDelta, ct);
        }

        private async Task<string> GenerateSinglePassOrContinuedAsync(
            NodeControl currentNode,
            string topText,
            string model,
            CancellationToken ct)
        {
            if (_router.GetProviderKind(model) == AiProviderKind.PerplexitySonar)
            {
                return await GeneratePerplexitySonarWithContinuationAsync(currentNode, topText, model, ct);
            }
            string instructions = BuildGeneralNodeInstructions(model);

            var prompt = BuildPromptForNode(currentNode, topText);

            return await GenerateWithContinuationAsync(
                model,
                instructions,
                async followUp =>
                {
                    return await BuildOpenAiUserContentAsync(currentNode, prompt + followUp, ct);
                },
                async followUp =>
                {
                    return await BuildClaudeUserContentAsync(currentNode, prompt + followUp, ct);
                },
                MainReplyMaxOutputTokens,
                ct);
        }

        private async Task<string> GenerateSinglePassOrContinuedStreamAsync(
            NodeControl currentNode,
            string topText,
            string model,
            Action<string> onDelta,
            CancellationToken ct)
        {
            if (_router.GetProviderKind(model) == AiProviderKind.PerplexitySonar)
            {
                return await GeneratePerplexitySonarWithContinuationStreamingAsync(currentNode, topText, model, onDelta, ct);
            }

            string instructions = BuildGeneralNodeInstructions(model);

            var prompt = BuildPromptForNode(currentNode, topText);

            return await GenerateWithContinuationStreamingAsync(
                model,
                instructions,
                async followUp =>
                {
                    return await BuildOpenAiUserContentAsync(currentNode, prompt + followUp, ct);
                },
                async followUp =>
                {
                    return await BuildClaudeUserContentAsync(currentNode, prompt + followUp, ct);
                },
                onDelta,
                MainReplyMaxOutputTokens,
                ct);
        }

        private async Task<string> GenerateWithContinuationAsync(
            string model,
            string instructions,
            Func<string, Task<List<object>>> buildOpenAiContentFactory,
            Func<string, Task<List<object>>> buildClaudeContentFactory,
            int maxOutputTokens,
            CancellationToken ct)
        {
            var finalText = new StringBuilder();
            bool useClaude = _router.GetProviderKind(model) == AiProviderKind.Claude;

            for (int round = 0; round < ContinuationMaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                string followUp;
                if (round == 0)
                {
                    followUp = "";
                }
                else
                {
                    followUp =
$@"

【你前一次已輸出的內容（不可重複，僅供接續）】
{finalText}

請直接從上一行未完成處繼續輸出。
不要重複前面內容。
若這次已完整完成，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";
                }

                string reply;

                if (useClaude)
                {
                    var claudeContent = await buildClaudeContentFactory(followUp);
                    reply = await _router.GetClaudeService(model).GenerateAsync(
                        instructions,
                        claudeContent,
                        maxOutputTokens,
                        ct);
                }
                else
                {
                    var openAiContent = await buildOpenAiContentFactory(followUp);
                    var input = new object[]
                    {
                        new
                        {
                            role = "user",
                            content = openAiContent.ToArray()
                        }
                    };

                    reply = await _router.GetOpenAiService(model).GenerateAsync(
                        instructions,
                        input,
                        maxOutputTokens,
                        ct);
                }

                if (string.IsNullOrWhiteSpace(reply))
                    break;

                bool ended = HasEndMarker(reply);
                string cleaned = RemoveEndMarker(reply);

                if (round == 0)
                {
                    finalText.Append(cleaned.Trim());
                }
                else
                {
                    var append = RemoveLeadingOverlap(finalText.ToString(), cleaned);
                    append = RemoveRepeatedBlocks(append);

                    if (!string.IsNullOrWhiteSpace(append))
                    {
                        if (!IsHighlySimilarByContainment(finalText.ToString(), append))
                        {
                            if (finalText.Length > 0 && !finalText.ToString().EndsWith("\n"))
                                finalText.AppendLine();

                            finalText.Append(append.Trim());
                        }
                    }
                }

                if (ended)
                    break;
            }

            return RemoveRepeatedBlocks(finalText.ToString().Trim());
        }

        private async Task<string> GenerateWithContinuationStreamingAsync(
            string model,
            string instructions,
            Func<string, Task<List<object>>> buildOpenAiContentFactory,
            Func<string, Task<List<object>>> buildClaudeContentFactory,
            Action<string> onDelta,
            int maxOutputTokens,
            CancellationToken ct)
        {
            var finalText = new StringBuilder();
            bool useClaude = _router.GetProviderKind(model) == AiProviderKind.Claude;

            for (int round = 0; round < ContinuationMaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                string followUp;
                if (round == 0)
                {
                    followUp = "";
                }
                else
                {
                    followUp =
$@"

【你前一次已輸出的內容（不可重複，僅供接續）】
{finalText}

請直接從上一行未完成處繼續輸出。
不要重複前面內容。
若這次已完整完成，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";
                }

                string reply;

                if (round == 0)
                {
                    if (useClaude)
                    {
                        var claudeContent = await buildClaudeContentFactory(followUp);
                        reply = await _router.GetClaudeService(model).GenerateStreamAsync(
                            instructions,
                            claudeContent,
                            delta => { onDelta?.Invoke(delta); },
                            maxOutputTokens,
                            ct);
                    }
                    else
                    {
                        var openAiContent = await buildOpenAiContentFactory(followUp);
                        var input = new object[]
                        {
                            new
                            {
                                role = "user",
                                content = openAiContent.ToArray()
                            }
                        };

                        reply = await _router.GetOpenAiService(model).GenerateStreamAsync(
                            instructions,
                            input,
                            delta => { onDelta?.Invoke(delta); },
                            maxOutputTokens,
                            ct);
                    }
                }
                else
                {
                    if (useClaude)
                    {
                        var claudeContent = await buildClaudeContentFactory(followUp);
                        reply = await _router.GetClaudeService(model).GenerateAsync(
                            instructions,
                            claudeContent,
                            maxOutputTokens,
                            ct);
                    }
                    else
                    {
                        var openAiContent = await buildOpenAiContentFactory(followUp);
                        var input = new object[]
                        {
                            new
                            {
                                role = "user",
                                content = openAiContent.ToArray()
                            }
                        };

                        reply = await _router.GetOpenAiService(model).GenerateAsync(
                            instructions,
                            input,
                            maxOutputTokens,
                            ct);
                    }
                }

                if (string.IsNullOrWhiteSpace(reply))
                    break;

                bool ended = HasEndMarker(reply);
                string cleaned = RemoveEndMarker(reply);

                if (round == 0)
                {
                    finalText.Append(cleaned.Trim());
                }
                else
                {
                    var append = RemoveLeadingOverlap(finalText.ToString(), cleaned);
                    append = RemoveRepeatedBlocks(append);

                    if (!string.IsNullOrWhiteSpace(append))
                    {
                        if (!IsHighlySimilarByContainment(finalText.ToString(), append))
                        {
                            if (finalText.Length > 0 && !finalText.ToString().EndsWith("\n"))
                            {
                                finalText.AppendLine();
                                onDelta?.Invoke(Environment.NewLine);
                            }

                            finalText.Append(append.Trim());
                            onDelta?.Invoke(append.Trim());
                        }
                    }
                }

                if (ended)
                    break;
            }

            return RemoveRepeatedBlocks(finalText.ToString().Trim());
        }

        private async Task<string> GeneratePerplexitySonarWithContinuationAsync(
            NodeControl currentNode,
            string topText,
            string model,
            CancellationToken ct)
        {
            var finalText = new StringBuilder();
            bool isDeepResearch = _router.IsPerplexityDeepResearchModel(model);
            string sonarModel = _router.MapPerplexitySonarModel(model);
            var svc = _router.GetPerplexitySonarService(sonarModel);

            string instructions = isDeepResearch
                ? "你是一個研究型節點內容生成助手。請直接輸出整理完成後的內容本身，使用繁體中文。不要重述題目，不要輸出前言，不要輸出思考流程。"
                : "你是一個即時搜尋型節點內容生成助手。請直接輸出完成結果本身，使用繁體中文。不要重述題目，不要輸出前言，不要輸出思考流程。";

            for (int round = 0; round < ContinuationMaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                string followUp = round == 0
                    ? ""
                    : $@"

【你前一次已輸出的內容（不可重複，僅供接續）】
{finalText}

請直接從上一行未完成處繼續輸出。
不要重複前面內容。
若這次已完整完成，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";

                string prompt = BuildPromptForPerplexitySonar(currentNode, topText, isDeepResearch) + followUp;

                string reply = await svc.GenerateAsync(
                    instructions,
                    prompt,
                    maxOutputTokens: MainReplyMaxOutputTokens,
                    ct: ct);

                if (string.IsNullOrWhiteSpace(reply))
                    break;

                bool ended = HasEndMarker(reply);
                string cleaned = RemoveEndMarker(reply);

                if (round == 0)
                {
                    finalText.Append(cleaned.Trim());
                }
                else
                {
                    var append = RemoveLeadingOverlap(finalText.ToString(), cleaned);
                    append = RemoveRepeatedBlocks(append);

                    if (!string.IsNullOrWhiteSpace(append))
                    {
                        if (!IsHighlySimilarByContainment(finalText.ToString(), append))
                        {
                            if (finalText.Length > 0 && !finalText.ToString().EndsWith("\n"))
                                finalText.AppendLine();

                            finalText.Append(append.Trim());
                        }
                    }
                }

                if (ended)
                    break;
            }

            return RemoveRepeatedBlocks(finalText.ToString().Trim());
        }

        private async Task<string> GeneratePerplexitySonarWithContinuationStreamingAsync(
            NodeControl currentNode,
            string topText,
            string model,
            Action<string> onDelta,
            CancellationToken ct)
        {
            var finalText = new StringBuilder();
            bool isDeepResearch = _router.IsPerplexityDeepResearchModel(model);
            string sonarModel = _router.MapPerplexitySonarModel(model);
            var svc = _router.GetPerplexitySonarService(sonarModel);

            string instructions = isDeepResearch
                ? "你是一個研究型節點內容生成助手。請直接輸出整理完成後的內容本身，使用繁體中文。不要重述題目，不要輸出前言，不要輸出思考流程。"
                : "你是一個即時搜尋型節點內容生成助手。請直接輸出完成結果本身，使用繁體中文。不要重述題目，不要輸出前言，不要輸出思考流程。";

            for (int round = 0; round < ContinuationMaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                string followUp = round == 0
                    ? ""
                    : $@"

【你前一次已輸出的內容（不可重複，僅供接續）】
{finalText}

請直接從上一行未完成處繼續輸出。
不要重複前面內容。
若這次已完整完成，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";

                string prompt = BuildPromptForPerplexitySonar(currentNode, topText, isDeepResearch) + followUp;

                string reply;

                if (round == 0)
                {
                    reply = await svc.GenerateStreamAsync(
                        instructions,
                        prompt,
                        onDelta,
                        maxOutputTokens: MainReplyMaxOutputTokens,
                        ct: ct);
                }
                else
                {
                    reply = await svc.GenerateAsync(
                        instructions,
                        prompt,
                        maxOutputTokens: MainReplyMaxOutputTokens,
                        ct: ct);
                }

                if (string.IsNullOrWhiteSpace(reply))
                    break;

                bool ended = HasEndMarker(reply);
                string cleaned = RemoveEndMarker(reply);

                if (round == 0)
                {
                    finalText.Append(cleaned.Trim());
                }
                else
                {
                    var append = RemoveLeadingOverlap(finalText.ToString(), cleaned);
                    append = RemoveRepeatedBlocks(append);

                    if (!string.IsNullOrWhiteSpace(append))
                    {
                        if (!IsHighlySimilarByContainment(finalText.ToString(), append))
                        {
                            if (finalText.Length > 0 && !finalText.ToString().EndsWith("\n"))
                            {
                                finalText.AppendLine();
                                onDelta?.Invoke(Environment.NewLine);
                            }

                            finalText.Append(append.Trim());
                            onDelta?.Invoke(append.Trim());
                        }
                    }
                }

                if (ended)
                    break;
            }

            return RemoveRepeatedBlocks(finalText.ToString().Trim());
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

            string instructions =
                "你是一個文件切段助手。請只輸出合法 JSON，不要輸出 markdown，不要加任何前後說明。";

            string raw;

            if (_router.GetProviderKind(model) == AiProviderKind.Claude)
            {
                var content = await BuildClaudeUserContentAsync(currentNode, discoveryPrompt, ct);
                raw = await _router.GetClaudeService(model).GenerateAsync(instructions, content, SegmentDiscoveryMaxTokens, ct);
            }
            else
            {
                var content = await BuildOpenAiUserContentAsync(currentNode, discoveryPrompt, ct);
                var input = new object[]
                {
                    new
                    {
                        role = "user",
                        content = content.ToArray()
                    }
                };

                raw = await _router.GetOpenAiService(model).GenerateAsync(instructions, input, SegmentDiscoveryMaxTokens, ct);
            }

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
                    string title = "";
                    string hint = "";

                    if (item.TryGetProperty("title", out var titleEl))
                        title = titleEl.GetString() ?? "";

                    if (item.TryGetProperty("hint", out var hintEl))
                        hint = hintEl.GetString() ?? "";

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

        private async Task<string> TranslateBySegmentsAsync(
            NodeControl currentNode,
            string topText,
            string model,
            CancellationToken ct)
        {
            var segments = await TryDiscoverSegmentsAsync(currentNode, topText, model, ct);
            if (segments.Count <= 1)
            {
                return await GenerateSinglePassOrContinuedAsync(currentNode, topText, model, ct);
            }

            var sb = new StringBuilder();

            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var seg = segments[i];
                int index = i + 1;

                string segmentPrompt =
$@"使用者要求：
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

                string instructions =
                    "你是一個文件分段翻譯助手。" +
                    "請直接輸出這一段翻譯完成後的內容本身。" +
                    "不要加入前言、摘要、操作說明或步驟。" +
                    "若遇到菜單、PDF 或附件，請只翻譯指定段落。" +
                    "若模型不確定分段邊界，也不得重複輸出前面已翻過的大段內容。";

                string translated = await GenerateWithContinuationAsync(
                    model,
                    instructions,
                    async followUp =>
                    {
                        return await BuildOpenAiUserContentAsync(currentNode, segmentPrompt + followUp, ct);
                    },
                    async followUp =>
                    {
                        return await BuildClaudeUserContentAsync(currentNode, segmentPrompt + followUp, ct);
                    },
                    SegmentTranslationMaxTokens,
                    ct);

                translated = RemoveRepeatedBlocks(translated.Trim());

                if (string.IsNullOrWhiteSpace(translated))
                    continue;

                if (SegmentLooksDuplicate(sb, translated))
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(translated);
            }

            var final = RemoveRepeatedBlocks(sb.ToString().Trim());

            if (string.IsNullOrWhiteSpace(final))
                return await GenerateSinglePassOrContinuedAsync(currentNode, topText, model, ct);

            return final;
        }

        private async Task<string> TranslateBySegmentsStreamAsync(
            NodeControl currentNode,
            string topText,
            string model,
            Action<string> onDelta,
            CancellationToken ct)
        {
            var segments = await TryDiscoverSegmentsAsync(currentNode, topText, model, ct);
            if (segments.Count <= 1)
            {
                return await GenerateSinglePassOrContinuedStreamAsync(currentNode, topText, model, onDelta, ct);
            }

            var sb = new StringBuilder();
            bool firstVisibleSegment = true;

            for (int i = 0; i < segments.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var seg = segments[i];
                int index = i + 1;

                string segmentPrompt =
$@"使用者要求：
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

                string instructions =
                    "你是一個文件分段翻譯助手。" +
                    "請直接輸出這一段翻譯完成後的內容本身。" +
                    "不要加入前言、摘要、操作說明或步驟。" +
                    "若遇到菜單、PDF 或附件，請只翻譯指定段落。" +
                    "若模型不確定分段邊界，也不得重複輸出前面已翻過的大段內容。";

                bool segmentStarted = false;

                string translated = await GenerateWithContinuationStreamingAsync(
                    model,
                    instructions,
                    async followUp =>
                    {
                        return await BuildOpenAiUserContentAsync(currentNode, segmentPrompt + followUp, ct);
                    },
                    async followUp =>
                    {
                        return await BuildClaudeUserContentAsync(currentNode, segmentPrompt + followUp, ct);
                    },
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
                    SegmentTranslationMaxTokens,
                    ct);

                translated = RemoveRepeatedBlocks(translated.Trim());

                if (string.IsNullOrWhiteSpace(translated))
                    continue;

                if (SegmentLooksDuplicate(sb, translated))
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(translated);
                firstVisibleSegment = false;
            }

            var final = RemoveRepeatedBlocks(sb.ToString().Trim());

            if (string.IsNullOrWhiteSpace(final))
                return await GenerateSinglePassOrContinuedStreamAsync(currentNode, topText, model, onDelta, ct);

            return final;
        }


        private sealed class PerplexityContextBundle
        {
            public string UpstreamContext { get; set; } = "";
            public string DownstreamContext { get; set; } = "";
            public string OtherNodesContext { get; set; } = "";
            public string AttachmentHint { get; set; } = "";
        }

        private string BuildPromptForNode(NodeControl current, string topText)
        {
            var ctx = BuildPerplexityContextBundle(current, includeOtherNodes: true);

            string primaryContext;
            if (string.IsNullOrWhiteSpace(ctx.UpstreamContext) && string.IsNullOrWhiteSpace(ctx.DownstreamContext))
            {
                primaryContext = "（此節點目前沒有連線上下游）";
            }
            else
            {
                var lines = new List<string>();

                if (!string.IsNullOrWhiteSpace(ctx.UpstreamContext))
                {
                    lines.Add("【上游（較重要）】");
                    lines.Add(ctx.UpstreamContext);
                }

                if (!string.IsNullOrWhiteSpace(ctx.DownstreamContext))
                {
                    lines.Add("【下游（較重要）】");
                    lines.Add(ctx.DownstreamContext);
                }

                primaryContext = string.Join("\n\n", lines);
            }

            string secondaryContext = string.IsNullOrWhiteSpace(ctx.OtherNodesContext)
                ? "（無其它節點）"
                : ctx.OtherNodesContext;

            return
$@"你正在一個節點式筆記檔案中工作。

【主要記憶：同一條連線上下游】
{primaryContext}

【次要參考：同檔案其它節點】
{secondaryContext}

【目前節點上半部內容】
{topText}
{ctx.AttachmentHint}

請直接輸出「完成後的內容本身」作為下半部結果。
不要輸出前言、不要解釋你要怎麼做、不要寫成工作流程或注意事項模板。
不要使用「以下是」「你可以依照以下步驟」「為確保完整」這類開頭。

規則：
1. 若上半部是翻譯需求：直接輸出翻譯結果。
2. 若上半部是整理/改寫需求：直接輸出整理完成版本。
3. 若上半部是提問：直接回答問題，再補必要說明。
4. 若附件是主要資訊來源：直接根據附件內容完成結果。
5. 除非上半部明確要求步驟，否則不要輸出流程式條列。
6. 完整輸出完成後，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private string BuildPromptForPerplexitySonar(NodeControl current, string topText, bool isDeepResearch)
        {
            var ctx = BuildPerplexityContextBundle(current, includeOtherNodes: isDeepResearch);

            if (isDeepResearch)
            {
                string upstreamPart = string.IsNullOrWhiteSpace(ctx.UpstreamContext)
                    ? "（無上游背景）"
                    : ctx.UpstreamContext;

                string downstreamPart = string.IsNullOrWhiteSpace(ctx.DownstreamContext)
                    ? "（目前沒有明確下游）"
                    : ctx.DownstreamContext;

                string otherNodesPart = string.IsNullOrWhiteSpace(ctx.OtherNodesContext)
                    ? "（無其它節點參考）"
                    : ctx.OtherNodesContext;

                return
$@"你正在處理一個節點式研究任務。
請先理解目前問題，再結合中度上下文鏈做較完整的研究、查證、補充與整理。
直接輸出結果本身，使用繁體中文。
不要重述題目，不要重述規則，不要輸出系統提示，不要輸出思考流程，不要寫前言。

【上游背景（中度重要）】
{upstreamPart}

【目前節點內容】
{topText}
{ctx.AttachmentHint}

【可參考的下游方向】
{downstreamPart}

【同檔案其它節點（低權重參考）】
{otherNodesPart}

要求：
1. 優先回答目前節點問題，不要被其它節點帶偏。
2. 若上游是前情提要、先前整理或研究方向，請承接它再深化。
3. 可進行查證、補充、比較、延伸分析，但仍要圍繞目前節點。
4. 若附件是主要來源，請把附件視為高權重背景。
5. 除非目前節點明確要求步驟，否則不要輸出流程式條列。
6. 若回答過長，請在本次輸出結尾單獨輸出 [[END_OF_RESPONSE]]。";
            }

            string compactUpstream = string.IsNullOrWhiteSpace(ctx.UpstreamContext)
                ? "（無上游背景）"
                : ctx.UpstreamContext;

            return
$@"你正在處理一個節點式即時搜尋 / 查證任務。
請以「目前節點問題」為主，並參考精簡版上游背景來回答。
直接輸出完成結果本身，使用繁體中文。
不要重述題目，不要重述規則，不要輸出系統提示，不要輸出思考流程，不要寫前言。

【精簡上游背景】
{compactUpstream}

【目前節點內容】
{topText}
{ctx.AttachmentHint}

要求：
1. 以目前節點問題為核心，優先做查證、補充、搜尋型回答。
2. 上游背景只用來理解脈絡，不要被上游語氣或格式綁住。
3. 若目前問題與上游不同，以目前問題為最高優先。
4. 若附件是主要來源，請優先根據附件與目前問題回答。
5. 除非目前節點明確要求步驟，否則不要輸出流程式條列。
6. 若回答過長，請在本次輸出結尾單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private PerplexityContextBundle BuildPerplexityContextBundle(NodeControl current, bool includeOtherNodes)
        {
            var bundle = new PerplexityContextBundle();

            var atts = _main.GetAttachmentsForNode(current);
            if (atts.Count > 0)
            {
                bundle.AttachmentHint =
                    "\n\n【本節點附件】\n" +
                    string.Join("\n", atts.Select(a => $"- ({a.Kind}) {a.FileName}"));
            }

            var upstream = CollectUpstream(current, 10);
            var downstream = CollectDownstream(current, 2);

            bundle.UpstreamContext = BuildContextSection(
                upstream,
                topLimit: 220,
                bottomLimit: 180,
                maxCount: includeOtherNodes ? 6 : 4);

            bundle.DownstreamContext = BuildContextSection(
                downstream,
                topLimit: 140,
                bottomLimit: 120,
                maxCount: includeOtherNodes ? 2 : 1);

            if (includeOtherNodes)
            {
                var mainSet = new HashSet<Guid> { current.Id };
                foreach (var n in upstream) mainSet.Add(n.Id);
                foreach (var n in downstream) mainSet.Add(n.Id);

                var others = _main.GetAllNodesInCanvas()
                    .Where(n => !mainSet.Contains(n.Id))
                    .Take(OtherNodeContextLimit)
                    .ToList();

                if (others.Count > 0)
                {
                    var lines = new List<string>
                    {
                        $"（以下為同檔案其它節點的低權重參考，最多顯示 {OtherNodeContextLimit} 個）"
                    };

                    foreach (var n in others)
                    {
                        var top = Truncate((n.GetTopText() ?? "").Trim(), 120);
                        var bottom = Truncate((n.GetBottomText() ?? "").Trim(), 120);
                        lines.Add($"- Node {n.Id}\nTop: {top}\nBottom: {bottom}".Trim());
                    }

                    bundle.OtherNodesContext = string.Join("\n\n", lines);
                }
            }

            return bundle;
        }

        private static string BuildContextSection(
            IEnumerable<NodeControl> nodes,
            int topLimit,
            int bottomLimit,
            int maxCount)
        {
            var list = nodes?
                .Take(maxCount)
                .Select(n =>
                {
                    var top = Truncate((n.GetTopText() ?? "").Trim(), topLimit);
                    var bottom = Truncate((n.GetBottomText() ?? "").Trim(), bottomLimit);

                    if (string.IsNullOrWhiteSpace(bottom))
                        return $"- Node {n.Id}\nTop: {top}".Trim();

                    return $"- Node {n.Id}\nTop: {top}\nBottom: {bottom}".Trim();
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (list == null || list.Count == 0)
                return "";

            return string.Join("\n\n", list);
        }

        private async Task<List<object>> BuildOpenAiUserContentAsync(
            NodeControl currentNode,
            string textPrompt,
            CancellationToken ct)
        {
            var contentList = new List<object>
            {
                new { type = "input_text", text = textPrompt }
            };

            var attachmentsRootDir = _main.GetAttachmentsRootDir();

            foreach (var a in _main.GetAttachmentsForNode(currentNode))
            {
                try
                {
                    var abs = Path.Combine(attachmentsRootDir, a.RelativePath);
                    if (!File.Exists(abs)) continue;

                    var bytes = await File.ReadAllBytesAsync(abs, ct).ConfigureAwait(true);
                    var dataUrl = ToDataUrl(bytes, a.MimeType);

                    if (string.Equals(a.Kind, "image", StringComparison.OrdinalIgnoreCase))
                    {
                        contentList.Add(new
                        {
                            type = "input_image",
                            image_url = dataUrl
                        });
                    }
                    else
                    {
                        contentList.Add(new
                        {
                            type = "input_file",
                            filename = a.FileName,
                            file_data = dataUrl
                        });
                    }
                }
                catch
                {
                }
            }

            return contentList;
        }

        private async Task<List<object>> BuildClaudeUserContentAsync(
            NodeControl currentNode,
            string textPrompt,
            CancellationToken ct)
        {
            var contentList = new List<object>();
            var attachmentsRootDir = _main.GetAttachmentsRootDir();

            foreach (var a in _main.GetAttachmentsForNode(currentNode))
            {
                try
                {
                    var abs = Path.Combine(attachmentsRootDir, a.RelativePath);
                    if (!File.Exists(abs)) continue;

                    var bytes = await File.ReadAllBytesAsync(abs, ct).ConfigureAwait(true);

                    if (string.Equals(a.Kind, "image", StringComparison.OrdinalIgnoreCase))
                    {
                        contentList.Add(ClaudeChatService.BuildImageBlock(bytes, a.MimeType));
                    }
                    else if (string.Equals(a.MimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                    {
                        contentList.Add(ClaudeChatService.BuildPdfBlock(bytes));
                    }
                    else
                    {
                        string text;
                        try
                        {
                            text = Encoding.UTF8.GetString(bytes);
                        }
                        catch
                        {
                            text = $"[無法以 UTF-8 讀取附件：{a.FileName}]";
                        }

                        contentList.Add(ClaudeChatService.BuildTextBlock(
                            $"【附件：{a.FileName}】\n{text}"));
                    }
                }
                catch
                {
                }
            }

            contentList.Add(ClaudeChatService.BuildTextBlock(textPrompt));
            return contentList;
        }

        private List<NodeControl> CollectUpstream(NodeControl start, int hops)
        {
            var result = new List<NodeControl>();
            var visited = new HashSet<Guid> { start.Id };

            var layer = new List<NodeControl> { start };
            for (int step = 0; step < hops; step++)
            {
                var next = new List<NodeControl>();
                foreach (var cur in layer)
                {
                    foreach (var inc in GetIncoming(cur))
                    {
                        var prev = inc.StartNode;
                        if (prev != null && visited.Add(prev.Id))
                        {
                            result.Add(prev);
                            next.Add(prev);
                        }
                    }
                }

                layer = next;
                if (layer.Count == 0) break;
            }

            return result;
        }

        private List<NodeControl> CollectDownstream(NodeControl start, int hops)
        {
            var result = new List<NodeControl>();
            var visited = new HashSet<Guid> { start.Id };

            var layer = new List<NodeControl> { start };
            for (int step = 0; step < hops; step++)
            {
                var next = new List<NodeControl>();
                foreach (var cur in layer)
                {
                    foreach (var outc in GetOutgoing(cur))
                    {
                        var nxt = outc.EndNode;
                        if (nxt != null && visited.Add(nxt.Id))
                        {
                            result.Add(nxt);
                            next.Add(nxt);
                        }
                    }
                }

                layer = next;
                if (layer.Count == 0) break;
            }

            return result;
        }

        private IEnumerable<ConnectionInfo> GetIncoming(NodeControl node)
            => GetConnections().Where(c => ReferenceEquals(c.EndNode, node));

        private IEnumerable<ConnectionInfo> GetOutgoing(NodeControl node)
            => GetConnections().Where(c => ReferenceEquals(c.StartNode, node));

        private IEnumerable<ConnectionInfo> GetConnections()
        {
            foreach (var c in _main.GetAllConnections())
            {
                yield return new ConnectionInfo
                {
                    StartNode = c.StartNode,
                    StartThumb = c.StartThumb,
                    EndNode = c.EndNode,
                    EndThumb = c.EndThumb
                };
            }
        }

        private bool HasNonImageAttachments(NodeControl node)
        {
            return _main.GetAttachmentsForNode(node)
                .Any(a => !string.Equals(a.Kind, "image", StringComparison.OrdinalIgnoreCase));
        }

        private static bool LooksLikeFullTranslationRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;

            string s = text.Replace(" ", "").Trim();

            string[] keys =
            {
                "完整翻譯", "全文翻譯", "全部翻譯", "整份翻譯", "整個翻譯",
                "完整中文", "完整菜單", "完整菜单", "整份pdf", "整個pdf",
                "翻譯整份", "翻譯全部", "翻譯全文", "請完整翻譯", "完整地翻譯"
            };

            return keys.Any(k => s.Contains(k, StringComparison.OrdinalIgnoreCase));
        }

        private static string ToDataUrl(byte[] bytes, string mime)
        {
            var b64 = Convert.ToBase64String(bytes);
            return $"data:{mime};base64,{b64}";
        }

        private static string RemoveEndMarker(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";
            return text.Replace("[[END_OF_RESPONSE]]", "").Trim();
        }

        private static bool HasEndMarker(string text)
        {
            return !string.IsNullOrWhiteSpace(text) &&
                   text.Contains("[[END_OF_RESPONSE]]", StringComparison.Ordinal);
        }

        private static string RemoveLeadingOverlap(string existing, string next)
        {
            if (string.IsNullOrWhiteSpace(next)) return "";
            if (string.IsNullOrWhiteSpace(existing)) return next.Trim();

            string a = existing.TrimEnd();
            string b = next.TrimStart();

            int max = Math.Min(a.Length, b.Length);
            for (int len = max; len >= 20; len--)
            {
                string tail = a.Substring(a.Length - len, len);
                if (b.StartsWith(tail, StringComparison.Ordinal))
                {
                    return b.Substring(len).TrimStart();
                }
            }

            var existingLines = a.Split('\n').Select(x => x.Trim()).Where(x => x.Length > 0).TakeLast(8).ToList();
            var nextLines = b.Split('\n').ToList();

            int skip = 0;
            while (skip < nextLines.Count && existingLines.Any(l => string.Equals(l, nextLines[skip].Trim(), StringComparison.Ordinal)))
            {
                skip++;
            }

            return string.Join("\n", nextLines.Skip(skip)).TrimStart();
        }

        private static string RemoveRepeatedBlocks(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var blocks = text
                .Replace("\r\n", "\n")
                .Split(new[] { "\n\n" }, StringSplitOptions.None)
                .Select(b => b.Trim())
                .Where(b => b.Length > 0)
                .ToList();

            var kept = new List<string>();

            foreach (var block in blocks)
            {
                bool duplicate = kept.Any(x => IsHighlySimilarByContainment(x, block));
                if (!duplicate)
                    kept.Add(block);
            }

            return string.Join("\n\n", kept).Trim();
        }

        private static bool SegmentLooksDuplicate(StringBuilder accumulated, string candidate)
        {
            if (accumulated.Length == 0 || string.IsNullOrWhiteSpace(candidate))
                return false;

            string existing = accumulated.ToString().Trim();
            string incoming = candidate.Trim();

            if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(incoming))
                return false;

            if (IsHighlySimilarByContainment(existing, incoming))
                return true;

            var parts = existing
                .Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.RemoveEmptyEntries)
                .Select(x => x.Trim())
                .Where(x => x.Length > 0)
                .ToList();

            foreach (var part in parts.TakeLast(8))
            {
                if (IsHighlySimilarByContainment(part, incoming))
                    return true;
            }

            return false;
        }

        private static string NormalizeForSimilarity(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
            s = Regex.Replace(s, @"\s+", " ");
            s = s.Trim().ToLowerInvariant();
            return s;
        }

        private static bool IsHighlySimilarByContainment(string existing, string candidate)
        {
            var a = NormalizeForSimilarity(existing);
            var b = NormalizeForSimilarity(candidate);

            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;

            if (a.Contains(b, StringComparison.Ordinal) || b.Contains(a, StringComparison.Ordinal))
                return true;

            if (a.Length < 80 || b.Length < 80)
                return false;

            int min = Math.Min(a.Length, b.Length);
            int max = Math.Max(a.Length, b.Length);
            double ratio = (double)min / max;

            if (ratio < 0.75)
                return false;

            int sampleLen = Math.Min(220, min);
            string aHead = a.Substring(0, sampleLen);
            string bHead = b.Substring(0, sampleLen);

            if (aHead == bHead)
                return true;

            int tailLen = Math.Min(220, min);
            string aTail = a.Substring(a.Length - tailLen, tailLen);
            string bTail = b.Substring(b.Length - tailLen, tailLen);

            return aTail == bTail;
        }

        private static string NodeBrief(NodeControl n)
        {
            var top = (n.GetTopText() ?? "").Trim();
            var bottom = (n.GetBottomText() ?? "").Trim();

            top = Truncate(top, 260);
            bottom = Truncate(bottom, 260);

            var locked = n.GetTopLocked() ? "Locked" : "Unlocked";
            return $"[{locked}] Top: {top}\nBottom: {bottom}".Trim();
        }

        private AiRouteInfo PrepareRoute(string? selectedModel)
        {
            var route = _router.GetRouteInfo(selectedModel);
            _router.EnsureServiceReady(route);
            return route;
        }

        private static string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "…";
        }
    }
}