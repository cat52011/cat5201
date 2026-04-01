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
        private readonly AiAutoModelResolverService _autoResolver;

        private const int MainReplyMaxOutputTokens = 8000;
        private const int ContinuationMaxRounds = 5;
        private const int SegmentDiscoveryMaxTokens = 1200;
        private const int SegmentTranslationMaxTokens = 8000;

        private enum NodeContextStrategy
        {
            Full = 0,
            CompactSearch = 1,
            Research = 2
        }

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

        private sealed class NodeContextBundle
        {
            public string UpstreamContext { get; set; } = "";
            public string DownstreamContext { get; set; } = "";
            public string BranchSummaryContext { get; set; } = "";
            public string AttachmentHint { get; set; } = "";
        }

        private sealed class ExecutionDecision
        {
            public string ModelId { get; set; } = "";
            public NodeTaskMode TaskMode { get; set; } = NodeTaskMode.Chat;
            public string ResolverLabel { get; set; } = "Manual";
            public string StatusLabel { get; set; } = "Manual";
            public double Confidence { get; set; }
            public bool UsedApiResolver { get; set; }
            public bool UsedFallbackToRules { get; set; }
        }

        public NodeService(AiServiceRouter router, MainWindow main)
        {
            _router = router;
            _main = main;
            _autoResolver = new AiAutoModelResolverService(router);
        }

        private static string GetRuntimeModelLabel(string model)
        {
            var def = AiModelHelper.GetDefinition(model);

            if (!string.IsNullOrWhiteSpace(def.DisplayName))
                return def.DisplayName;

            if (!string.IsNullOrWhiteSpace(def.Id))
                return def.Id;

            return AiModelRegistry.Default.DisplayName;
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
不要把自己說成別的模型，不要把自己統稱為 OpenAI，也不要捏造未提供的型號。
若使用者沒有詢問模型身分，就不要主動提起。";
        }

        private static string BuildContinuationEndMarkerInstruction()
        {
            return "\n\n完整輸出完成後，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private static string BuildGeneralNodeInstructions(string model, NodeTaskMode taskMode)
        {
            return
                "你是一個專業的節點內容生成助手。" +
                "請直接完成目前節點上半部要求的內容，不要先寫任務流程、操作步驟、整理原則、校對流程、備份說明或前言。" +
                "除非使用者明確要求步驟說明，否則請直接輸出結果本身。" +
                "若是翻譯需求，就直接翻譯；若是整理需求，就直接整理完成內容；若是問答需求，就直接回答。" +
                "回應請使用繁體中文。" +
                "可以參考主鏈上下游與支線摘要，但不要被支線帶偏。" +
                "若有附件（圖片/檔案），請閱讀後直接根據附件內容作答。" +
                BuildTaskModeInstruction(taskMode) +
                BuildModelIdentityGuard(model) +
                BuildContinuationEndMarkerInstruction();
        }

        private static string BuildPerplexityInstructions(string model, bool isDeepResearch, NodeTaskMode taskMode)
        {
            string baseText = isDeepResearch
                ? "你是一個研究型節點內容助手。請直接輸出整理完成後的內容本身，使用繁體中文。不要重述題目，不要輸出前言，不要輸出思考流程。"
                : "你是一個搜尋型節點內容助手。請直接輸出完成結果本身，使用繁體中文。不要重述題目，不要輸出前言，不要輸出思考流程。";

            return
                baseText +
                BuildTaskModeInstruction(taskMode) +
                BuildModelIdentityGuard(model) +
                BuildContinuationEndMarkerInstruction();
        }

        private static string BuildTaskModeInstruction(NodeTaskMode taskMode)
        {
            return taskMode switch
            {
                NodeTaskMode.Translate =>
                    "目前任務模式是 Translate。請把重點放在忠實翻譯、原意保留、格式清楚、不要額外延伸評論。",

                NodeTaskMode.Research =>
                    "目前任務模式是 Research。請把重點放在查證、比較、補充背景與整理可信資訊。",

                NodeTaskMode.Summarize =>
                    "目前任務模式是 Summarize。請把重點放在濃縮重點、保留核心資訊、避免冗長。",

                NodeTaskMode.Rewrite =>
                    "目前任務模式是 Rewrite。請把重點放在重寫、潤稿、調整語氣與改善可讀性。",

                NodeTaskMode.Extract =>
                    "目前任務模式是 Extract。請把重點放在抽取欄位、擷取結構化資訊、避免多餘延伸。",

                NodeTaskMode.Code =>
                    "目前任務模式是 Code。請把重點放在程式正確性、可貼上使用、維持既有架構並清楚說明必要修改。",

                _ =>
                    "目前任務模式是 Chat。請直接回應使用者需求並完成內容。"
            };
        }

        private static string BuildSegmentDiscoveryInstructions()
        {
            return
                "你是一個文件段落規劃助手。" +
                "請根據附件文件本身的實際內容，按原始順序拆分為適合逐段處理的邏輯段落。" +
                "不要虛構文件不存在的章節，不要加入品牌或模型自我介紹。" +
                "請只輸出合法 JSON，不要輸出 markdown，不要加任何前後說明。";
        }

        private static string BuildSegmentTranslationInstructions()
        {
            return
                "你是一個文件分段翻譯助手。" +
                "請直接輸出這一段翻譯完成後的內容本身。" +
                "不要加入前言、摘要、操作說明或步驟。" +
                "若遇到菜單、PDF 或附件，請只翻譯指定段落。" +
                "若模型不確定分段邊界，也不得重複輸出前面已翻過的大段內容。" +
                "不要主動宣稱自己屬於任何特定品牌、公司或模型。";
        }

        public async Task<string> GenerateAsync(NodeControl node, string topText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return "";

            var decision = await ResolveExecutionDecisionAsync(node, topText, ct);
            ApplyDecisionVisualization(decision);
            SyncActualModelToNode(node, decision);

            var route = PrepareRoute(decision.ModelId);
            string model = route.NodeModel;

            if (route.Provider == AiProviderKind.PerplexitySonar)
            {
                return await GenerateSinglePassOrContinuedAsync(node, topText, model, decision.TaskMode, ct);
            }

            bool useSegmentMode =
                decision.TaskMode == NodeTaskMode.Translate &&
                LooksLikeFullTranslationRequest(topText) &&
                HasNonImageAttachments(node);

            if (useSegmentMode)
            {
                try
                {
                    var segmented = await TranslateBySegmentsAsync(node, topText, model, decision.TaskMode, ct);
                    if (!string.IsNullOrWhiteSpace(segmented))
                        return segmented;
                }
                catch
                {
                }
            }

            return await GenerateSinglePassOrContinuedAsync(node, topText, model, decision.TaskMode, ct);
        }

        public async Task<string> GenerateStreamAsync(
            NodeControl node,
            string topText,
            Action<string> onDelta,
            CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return "";

            var decision = await ResolveExecutionDecisionAsync(node, topText, ct);
            ApplyDecisionVisualization(decision);
            SyncActualModelToNode(node, decision);

            var route = PrepareRoute(decision.ModelId);
            string model = route.NodeModel;

            if (route.Provider == AiProviderKind.PerplexitySonar)
            {
                return await GenerateSinglePassOrContinuedStreamAsync(node, topText, model, decision.TaskMode, onDelta, ct);
            }

            bool useSegmentMode =
                decision.TaskMode == NodeTaskMode.Translate &&
                LooksLikeFullTranslationRequest(topText) &&
                HasNonImageAttachments(node);

            if (useSegmentMode)
            {
                try
                {
                    var segmented = await TranslateBySegmentsStreamAsync(node, topText, model, decision.TaskMode, onDelta, ct);
                    if (!string.IsNullOrWhiteSpace(segmented))
                        return segmented;
                }
                catch
                {
                }
            }

            return await GenerateSinglePassOrContinuedStreamAsync(node, topText, model, decision.TaskMode, onDelta, ct);
        }

        private async Task<ExecutionDecision> ResolveExecutionDecisionAsync(
            NodeControl node,
            string topText,
            CancellationToken ct)
        {
            string selectedModel = _main.GetNodeSelectedModel(node);

            if (!_main.IsAutoModelSelectionEnabled())
            {
                var manualTask = ResolveAndPersistTaskMode(node, topText);

                return new ExecutionDecision
                {
                    ModelId = AiModelHelper.NormalizeNodeModel(selectedModel),
                    TaskMode = manualTask.Mode,
                    ResolverLabel = "Manual",
                    StatusLabel = "Manual",
                    Confidence = 1.0,
                    UsedApiResolver = false,
                    UsedFallbackToRules = false
                };
            }

            if (_main.IsAdvancedAutoResolverEnabled())
            {
                try
                {
                    var resolution = await _autoResolver.ResolveAsync(
                        topText,
                        _main.GetAttachmentsForNode(node),
                        ct);

                    var resolvedMode = NodeTaskModeHelper.Normalize(resolution.TaskMode);
                    _main.SetNodeTaskMode(node, resolvedMode);

                    string resolvedModel = AiModelHelper.NormalizeNodeModel(resolution.RecommendedModel);

                    return new ExecutionDecision
                    {
                        ModelId = resolvedModel,
                        TaskMode = resolvedMode,
                        ResolverLabel = "Responses API",
                        StatusLabel = "API Auto",
                        Confidence = resolution.Confidence,
                        UsedApiResolver = true,
                        UsedFallbackToRules = false
                    };
                }
                catch
                {
                    var fallbackTask = ResolveAndPersistTaskMode(node, topText);
                    string fallbackModel = _main.GetEffectiveNodeModel(node, topText);

                    return new ExecutionDecision
                    {
                        ModelId = AiModelHelper.NormalizeNodeModel(fallbackModel),
                        TaskMode = fallbackTask.Mode,
                        ResolverLabel = "Rules (fallback)",
                        StatusLabel = "API Auto",
                        Confidence = 0.30,
                        UsedApiResolver = true,
                        UsedFallbackToRules = true
                    };
                }
            }

            var ruleTask = ResolveAndPersistTaskMode(node, topText);
            string autoModel = _main.GetEffectiveNodeModel(node, topText);

            return new ExecutionDecision
            {
                ModelId = AiModelHelper.NormalizeNodeModel(autoModel),
                TaskMode = ruleTask.Mode,
                ResolverLabel = "Rules",
                StatusLabel = "Rule Auto",
                Confidence = 0.80,
                UsedApiResolver = false,
                UsedFallbackToRules = false
            };
        }

        private void ApplyDecisionVisualization(ExecutionDecision decision)
        {
            string modelLabel = GetRuntimeModelLabel(decision.ModelId);
            string taskLabel = NodeTaskModeHelper.ToDisplayName(decision.TaskMode);
            string confidenceText = $"{decision.Confidence:0.00}";
            string taskSummary = $"{taskLabel} / {confidenceText}";

            if (decision.UsedApiResolver)
            {
                if (decision.UsedFallbackToRules)
                {
                    _main.SetDecisionVisualization(
                        status: decision.StatusLabel,
                        mode: "Auto",
                        resolver: decision.ResolverLabel,
                        model: modelLabel,
                        taskSummary: taskSummary,
                        statusBrushHex: "#FFF4E8",
                        statusTextBrushHex: "#9A5A00");
                    return;
                }

                _main.SetDecisionVisualization(
                    status: decision.StatusLabel,
                    mode: "Auto",
                    resolver: decision.ResolverLabel,
                    model: modelLabel,
                    taskSummary: taskSummary,
                    statusBrushHex: "#EAF4FF",
                    statusTextBrushHex: "#245A9B");
                return;
            }

            if (_main.IsAutoModelSelectionEnabled())
            {
                _main.SetDecisionVisualization(
                    status: decision.StatusLabel,
                    mode: "Auto",
                    resolver: decision.ResolverLabel,
                    model: modelLabel,
                    taskSummary: taskSummary,
                    statusBrushHex: "#EEF7EA",
                    statusTextBrushHex: "#2E6A2E");
                return;
            }

            _main.SetDecisionVisualization(
                status: decision.StatusLabel,
                mode: "Manual",
                resolver: decision.ResolverLabel,
                model: modelLabel,
                taskSummary: taskSummary,
                statusBrushHex: "#EDEDED",
                statusTextBrushHex: "#404040");
        }

        private NodeTaskModeResolution ResolveAndPersistTaskMode(NodeControl node, string topText)
        {
            var resolution = NodeTaskModeResolver.Resolve(topText);
            _main.SetNodeTaskMode(node, resolution.Mode);
            return resolution;
        }

        private async Task<string> GenerateSinglePassOrContinuedAsync(
    NodeControl currentNode,
    string topText,
    string model,
    NodeTaskMode taskMode,
    CancellationToken ct)
        {
            var route = _router.GetRouteInfo(model);

            string instructions = route.Provider == AiProviderKind.PerplexitySonar
                ? BuildPerplexityInstructions(model, route.IsDeepResearch, taskMode)
                : BuildGeneralNodeInstructions(model, taskMode);

            string prompt = BuildPromptForNode(currentNode, topText, taskMode, GetContextStrategy(model));

            return await GenerateWithContinuationAsync(
                model,
                taskMode,
                instructions,
                async followUp => await BuildAiRequestAsync(
                    currentNode,
                    model,
                    instructions,
                    prompt + followUp,
                    taskMode,
                    useStreaming: false,
                    maxOutputTokens: MainReplyMaxOutputTokens,
                    ct),
                MainReplyMaxOutputTokens,
                ct);
        }

        private async Task<string> GenerateSinglePassOrContinuedStreamAsync(
    NodeControl currentNode,
    string topText,
    string model,
    NodeTaskMode taskMode,
    Action<string> onDelta,
    CancellationToken ct)
        {
            var route = _router.GetRouteInfo(model);

            string instructions = route.Provider == AiProviderKind.PerplexitySonar
                ? BuildPerplexityInstructions(model, route.IsDeepResearch, taskMode)
                : BuildGeneralNodeInstructions(model, taskMode);

            string prompt = BuildPromptForNode(currentNode, topText, taskMode, GetContextStrategy(model));

            return await GenerateWithContinuationStreamingAsync(
                model,
                taskMode,
                instructions,
                async followUp => await BuildAiRequestAsync(
                    currentNode,
                    model,
                    instructions,
                    prompt + followUp,
                    taskMode,
                    useStreaming: true,
                    maxOutputTokens: MainReplyMaxOutputTokens,
                    ct),
                onDelta,
                MainReplyMaxOutputTokens,
                ct);
        }

        private async Task<string> GenerateWithContinuationAsync(
    string model,
    NodeTaskMode taskMode,
    string instructions,
    Func<string, Task<AiRequest>> buildRequestFactory,
    int maxOutputTokens,
    CancellationToken ct)
        {
            var finalText = new StringBuilder();
            var provider = _router.GetProvider(model);

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

                var request = await buildRequestFactory(followUp);
                var response = await provider.GenerateAsync(request, ct);
                string reply = response.Text;

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

                    if (!string.IsNullOrWhiteSpace(append) &&
                        !IsHighlySimilarByContainment(finalText.ToString(), append))
                    {
                        if (finalText.Length > 0 && !finalText.ToString().EndsWith("\n"))
                            finalText.AppendLine();

                        finalText.Append(append.Trim());
                    }
                }

                if (ended)
                    break;
            }

            return RemoveRepeatedBlocks(finalText.ToString().Trim());
        }

        private async Task<string> GenerateWithContinuationStreamingAsync(
    string model,
    NodeTaskMode taskMode,
    string instructions,
    Func<string, Task<AiRequest>> buildRequestFactory,
    Action<string> onDelta,
    int maxOutputTokens,
    CancellationToken ct)
        {
            var finalText = new StringBuilder();
            var provider = _router.GetProvider(model);

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

                var request = await buildRequestFactory(followUp);
                string reply;

                if (round == 0)
                {
                    var streamed = await provider.GenerateStreamAsync(request, delta => onDelta?.Invoke(delta), ct);
                    reply = streamed.Text;
                }
                else
                {
                    var normal = await provider.GenerateAsync(request, ct);
                    reply = normal.Text;
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

                    if (!string.IsNullOrWhiteSpace(append) &&
                        !IsHighlySimilarByContainment(finalText.ToString(), append))
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

            string instructions = BuildSegmentDiscoveryInstructions();

            var request = await BuildAiRequestAsync(
                currentNode,
                model,
                instructions,
                discoveryPrompt,
                NodeTaskMode.Translate,
                useStreaming: false,
                maxOutputTokens: SegmentDiscoveryMaxTokens,
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

        private async Task<string> TranslateBySegmentsAsync(
            NodeControl currentNode,
            string topText,
            string model,
            NodeTaskMode taskMode,
            CancellationToken ct)
        {
            var segments = await TryDiscoverSegmentsAsync(currentNode, topText, model, ct);
            if (segments.Count <= 1)
            {
                return await GenerateSinglePassOrContinuedAsync(currentNode, topText, model, taskMode, ct);
            }

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

                string instructions = BuildSegmentTranslationInstructions();

                string translated = await GenerateWithContinuationAsync(
    model,
    taskMode,
    instructions,
    async followUp => await BuildAiRequestAsync(
        currentNode,
        model,
        instructions,
        segmentPrompt + followUp,
        taskMode,
        useStreaming: false,
        maxOutputTokens: SegmentTranslationMaxTokens,
        ct),
    SegmentTranslationMaxTokens,
    ct);

                translated = RemoveRepeatedBlocks(translated.Trim());

                if (string.IsNullOrWhiteSpace(translated) || SegmentLooksDuplicate(sb, translated))
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(translated);
            }

            var final = RemoveRepeatedBlocks(sb.ToString().Trim());
            if (string.IsNullOrWhiteSpace(final))
                return await GenerateSinglePassOrContinuedAsync(currentNode, topText, model, taskMode, ct);

            return final;
        }

        private async Task<string> TranslateBySegmentsStreamAsync(
            NodeControl currentNode,
            string topText,
            string model,
            NodeTaskMode taskMode,
            Action<string> onDelta,
            CancellationToken ct)
        {
            var segments = await TryDiscoverSegmentsAsync(currentNode, topText, model, ct);
            if (segments.Count <= 1)
            {
                return await GenerateSinglePassOrContinuedStreamAsync(currentNode, topText, model, taskMode, onDelta, ct);
            }

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

                string instructions = BuildSegmentTranslationInstructions();
                bool segmentStarted = false;

                string translated = await GenerateWithContinuationStreamingAsync(
    model,
    taskMode,
    instructions,
    async followUp => await BuildAiRequestAsync(
        currentNode,
        model,
        instructions,
        segmentPrompt + followUp,
        taskMode,
        useStreaming: true,
        maxOutputTokens: SegmentTranslationMaxTokens,
        ct),
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

                if (string.IsNullOrWhiteSpace(translated) || SegmentLooksDuplicate(sb, translated))
                    continue;

                if (sb.Length > 0)
                    sb.AppendLine().AppendLine();

                sb.Append(translated);
                firstVisibleSegment = false;
            }

            var final = RemoveRepeatedBlocks(sb.ToString().Trim());
            if (string.IsNullOrWhiteSpace(final))
                return await GenerateSinglePassOrContinuedStreamAsync(currentNode, topText, model, taskMode, onDelta, ct);

            return final;
        }

        private NodeContextStrategy GetContextStrategy(string model)
        {
            if (_router.IsPerplexityDeepResearchModel(model))
                return NodeContextStrategy.Research;

            if (_router.IsPerplexitySonarModel(model))
                return NodeContextStrategy.CompactSearch;

            return NodeContextStrategy.Full;
        }

        private string BuildPromptForNode(NodeControl current, string topText, NodeTaskMode taskMode, NodeContextStrategy strategy)
        {
            var ctx = BuildContextBundle(current, strategy);

            return strategy switch
            {
                NodeContextStrategy.CompactSearch => BuildCompactSearchPrompt(ctx, topText, taskMode),
                NodeContextStrategy.Research => BuildResearchPrompt(ctx, topText, taskMode),
                _ => BuildFullContextPrompt(ctx, topText, taskMode)
            };
        }

        private NodeContextBundle BuildContextBundle(NodeControl current, NodeContextStrategy strategy)
        {
            return strategy switch
            {
                NodeContextStrategy.CompactSearch => BuildCompactSearchContextBundle(current),
                NodeContextStrategy.Research => BuildResearchContextBundle(current),
                _ => BuildFullContextBundle(current)
            };
        }

        private NodeContextBundle BuildFullContextBundle(NodeControl current)
        {
            var bundle = CreateBaseContextBundle(current);

            var upstream = CollectUpstream(current, 20);
            var downstream = CollectDownstream(current, 6);

            bundle.UpstreamContext = BuildContextSection(
                upstream,
                topLimit: 1200,
                bottomLimit: 1200,
                maxCount: int.MaxValue);

            bundle.DownstreamContext = BuildContextSection(
                downstream,
                topLimit: 700,
                bottomLimit: 700,
                maxCount: int.MaxValue);

            bundle.BranchSummaryContext = BuildBranchSummaryContext(
                current,
                upstream,
                downstream,
                representativeCountPerBranch: 3,
                summaryTopLimit: 120,
                summaryBottomLimit: 100);

            return bundle;
        }

        private NodeContextBundle BuildCompactSearchContextBundle(NodeControl current)
        {
            var bundle = CreateBaseContextBundle(current);

            var upstream = CollectUpstream(current, 12);
            var downstream = CollectDownstream(current, 3);

            bundle.UpstreamContext = BuildContextSection(
                upstream,
                topLimit: 700,
                bottomLimit: 500,
                maxCount: int.MaxValue);

            bundle.DownstreamContext = BuildContextSection(
                downstream,
                topLimit: 320,
                bottomLimit: 240,
                maxCount: int.MaxValue);

            bundle.BranchSummaryContext = BuildBranchSummaryContext(
                current,
                upstream,
                downstream,
                representativeCountPerBranch: 2,
                summaryTopLimit: 70,
                summaryBottomLimit: 60);

            return bundle;
        }

        private void SyncActualModelToNode(NodeControl node, ExecutionDecision decision)
        {
            if (node == null || decision == null)
                return;

            string actualModel = AiModelHelper.NormalizeNodeModel(decision.ModelId);

            // 讓節點顯示真正使用的模型
            node.SetCommittedModelId(actualModel, syncEditingModel: true);

            // 讓 MainWindow 的 node model 狀態也同步
            _main.SetNodeSelectedModel(node, actualModel);

            // 刷新節點 UI
            node.RefreshModelSelectionUI();
        }

        private NodeContextBundle BuildResearchContextBundle(NodeControl current)
        {
            var bundle = CreateBaseContextBundle(current);

            var upstream = CollectUpstream(current, 20);
            var downstream = CollectDownstream(current, 6);

            bundle.UpstreamContext = BuildContextSection(
                upstream,
                topLimit: 1100,
                bottomLimit: 1000,
                maxCount: int.MaxValue);

            bundle.DownstreamContext = BuildContextSection(
                downstream,
                topLimit: 520,
                bottomLimit: 420,
                maxCount: int.MaxValue);

            bundle.BranchSummaryContext = BuildBranchSummaryContext(
                current,
                upstream,
                downstream,
                representativeCountPerBranch: 4,
                summaryTopLimit: 120,
                summaryBottomLimit: 110);

            return bundle;
        }

        private NodeContextBundle CreateBaseContextBundle(NodeControl current)
        {
            var bundle = new NodeContextBundle();

            var atts = _main.GetAttachmentsForNode(current);
            if (atts.Count > 0)
            {
                bundle.AttachmentHint =
                    "\n\n【本節點附件】\n" +
                    string.Join("\n", atts.Select(a => $"- ({a.Kind}) {a.FileName}"));
            }

            return bundle;
        }

        private string BuildBranchSummaryContext(
            NodeControl current,
            IEnumerable<NodeControl> upstream,
            IEnumerable<NodeControl> downstream,
            int representativeCountPerBranch,
            int summaryTopLimit,
            int summaryBottomLimit)
        {
            var excluded = new HashSet<Guid> { current.Id };
            foreach (var n in upstream) excluded.Add(n.Id);
            foreach (var n in downstream) excluded.Add(n.Id);

            var allOthers = _main.GetAllNodesInCanvas()
                .Where(n => !excluded.Contains(n.Id))
                .ToList();

            if (allOthers.Count == 0)
                return "";

            var visited = new HashSet<Guid>();
            var branchGroups = new List<List<NodeControl>>();

            foreach (var node in allOthers)
            {
                if (!visited.Add(node.Id))
                    continue;

                var group = CollectUndirectedConnectedGroup(node, excluded);
                foreach (var g in group)
                    visited.Add(g.Id);

                if (group.Count > 0)
                    branchGroups.Add(group);
            }

            if (branchGroups.Count == 0)
                return "";

            var lines = new List<string>
            {
                $"（以下為其它支線摘要，共 {branchGroups.Count} 條。僅供理解全局，不可蓋過目前節點與主鏈。）"
            };

            int branchIndex = 1;
            foreach (var group in branchGroups.OrderByDescending(g => g.Count))
            {
                var representatives = group
                    .OrderByDescending(n => ScoreNodeForSummary(n))
                    .Take(Math.Max(1, representativeCountPerBranch))
                    .ToList();

                var summaryParts = new List<string>();
                foreach (var n in representatives)
                {
                    var top = Truncate((n.GetTopText() ?? "").Trim(), summaryTopLimit);
                    var bottom = Truncate((n.GetBottomText() ?? "").Trim(), summaryBottomLimit);

                    if (!string.IsNullOrWhiteSpace(top) && !string.IsNullOrWhiteSpace(bottom))
                        summaryParts.Add($"Top: {top} / Bottom: {bottom}");
                    else if (!string.IsNullOrWhiteSpace(top))
                        summaryParts.Add($"Top: {top}");
                    else if (!string.IsNullOrWhiteSpace(bottom))
                        summaryParts.Add($"Bottom: {bottom}");
                }

                if (summaryParts.Count == 0)
                    continue;

                lines.Add($"- 支線 {branchIndex}（{group.Count} 節點）");
                foreach (var part in summaryParts)
                    lines.Add($"  • {part}");

                branchIndex++;
            }

            return string.Join("\n", lines);
        }

        private List<NodeControl> CollectUndirectedConnectedGroup(NodeControl seed, HashSet<Guid> excluded)
        {
            var result = new List<NodeControl>();
            var queue = new Queue<NodeControl>();
            var visited = new HashSet<Guid>();

            queue.Enqueue(seed);
            visited.Add(seed.Id);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);

                foreach (var next in GetUndirectedNeighbors(current))
                {
                    if (next == null) continue;
                    if (excluded.Contains(next.Id)) continue;
                    if (!visited.Add(next.Id)) continue;

                    queue.Enqueue(next);
                }
            }

            return result;
        }

        private IEnumerable<NodeControl> GetUndirectedNeighbors(NodeControl node)
        {
            foreach (var c in GetConnections())
            {
                if (ReferenceEquals(c.StartNode, node) && c.EndNode != null)
                    yield return c.EndNode;

                if (ReferenceEquals(c.EndNode, node) && c.StartNode != null)
                    yield return c.StartNode;
            }
        }

        private static int ScoreNodeForSummary(NodeControl n)
        {
            int score = 0;
            var top = (n.GetTopText() ?? "").Trim();
            var bottom = (n.GetBottomText() ?? "").Trim();

            score += Math.Min(top.Length, 200);
            score += Math.Min(bottom.Length, 120);

            if (n.GetTopLocked())
                score += 30;

            return score;
        }

        private string BuildFullContextPrompt(NodeContextBundle ctx, string topText, NodeTaskMode taskMode)
        {
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
                    lines.Add("【上游主鏈（最高權重）】");
                    lines.Add(ctx.UpstreamContext);
                }

                if (!string.IsNullOrWhiteSpace(ctx.DownstreamContext))
                {
                    lines.Add("【下游主鏈（高權重）】");
                    lines.Add(ctx.DownstreamContext);
                }

                primaryContext = string.Join("\n\n", lines);
            }

            string branchContext = string.IsNullOrWhiteSpace(ctx.BranchSummaryContext)
                ? "（無其它支線）"
                : ctx.BranchSummaryContext;

            return
$@"你正在一個節點式筆記檔案中工作。

【系統判定任務模式】
{taskMode}

【主鏈上下游】
{primaryContext}

【其它支線摘要（低權重）】
{branchContext}

【目前節點上半部內容】
{topText}
{ctx.AttachmentHint}

要求：
1. 目前節點內容是最高優先。
2. 主鏈上下游是高權重背景，請優先承接。
3. 其它支線摘要只用來理解全局，不可蓋過目前節點與主鏈。
4. 若支線與主鏈衝突，以目前節點與主鏈為準。
5. 直接輸出完成後的內容本身，不要寫前言、規則重述、流程說明。
6. 除非使用者明確要求步驟，否則不要輸出流程式條列。
7. 完整輸出完成後，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private string BuildCompactSearchPrompt(NodeContextBundle ctx, string topText, NodeTaskMode taskMode)
        {
            string compactUpstream = string.IsNullOrWhiteSpace(ctx.UpstreamContext)
                ? "（無上游主鏈）"
                : ctx.UpstreamContext;

            string compactDownstream = string.IsNullOrWhiteSpace(ctx.DownstreamContext)
                ? "（無下游主鏈）"
                : ctx.DownstreamContext;

            string compactBranches = string.IsNullOrWhiteSpace(ctx.BranchSummaryContext)
                ? "（無其它支線）"
                : ctx.BranchSummaryContext;

            return
$@"你正在處理一個節點式即時搜尋 / 查證任務。
請以目前節點問題為主，並參考主鏈與支線摘要回答。
直接輸出完成結果本身，使用繁體中文。
不要重述題目，不要重述規則，不要輸出系統提示，不要輸出思考流程，不要寫前言。

【系統判定任務模式】
{taskMode}

【上游主鏈（較重要）】
{compactUpstream}

【下游主鏈（可參考）】
{compactDownstream}

【其它支線摘要（低權重）】
{compactBranches}

【目前節點內容】
{topText}
{ctx.AttachmentHint}

要求：
1. 目前節點問題最高優先。
2. 主鏈比支線重要。
3. 支線摘要只用來理解大方向，不可主導回答。
4. 若任務模式是 Translate / Summarize / Rewrite / Extract / Code，也要輸出對應結果型態。
5. 若附件是主要來源，請優先根據附件與目前節點回答。
6. 若回答過長，請在本次輸出結尾單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private string BuildResearchPrompt(NodeContextBundle ctx, string topText, NodeTaskMode taskMode)
        {
            string upstreamPart = string.IsNullOrWhiteSpace(ctx.UpstreamContext)
                ? "（無上游主鏈）"
                : ctx.UpstreamContext;

            string downstreamPart = string.IsNullOrWhiteSpace(ctx.DownstreamContext)
                ? "（目前沒有明確下游）"
                : ctx.DownstreamContext;

            string branchPart = string.IsNullOrWhiteSpace(ctx.BranchSummaryContext)
                ? "（無其它支線）"
                : ctx.BranchSummaryContext;

            return
$@"你正在處理一個節點式研究任務。
請先理解目前問題，再結合主鏈與支線摘要進行較完整的研究、查證、補充與整理。
直接輸出結果本身，使用繁體中文。
不要重述題目，不要重述規則，不要輸出系統提示，不要輸出思考流程，不要寫前言。

【系統判定任務模式】
{taskMode}

【上游主鏈（高權重）】
{upstreamPart}

【目前節點內容】
{topText}
{ctx.AttachmentHint}

【下游主鏈方向（可參考）】
{downstreamPart}

【其它支線摘要（低權重）】
{branchPart}

要求：
1. 優先回答目前節點問題。
2. 承接主鏈上下游的脈絡與研究方向。
3. 支線摘要只用來幫助理解全局，不可取代主鏈。
4. 可進行查證、比較、補充、延伸分析，但仍要圍繞目前節點。
5. 若附件是主要來源，請把附件視為高權重背景。
6. 若回答過長，請在本次輸出結尾單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private static string BuildContextSection(
            IEnumerable<NodeControl> nodes,
            int topLimit,
            int bottomLimit,
            int maxCount)
        {
            var source = nodes ?? Enumerable.Empty<NodeControl>();
            if (maxCount != int.MaxValue)
                source = source.Take(maxCount);

            var list = source
                .Select(n =>
                {
                    var top = Truncate((n.GetTopText() ?? "").Trim(), topLimit);
                    var bottom = Truncate((n.GetBottomText() ?? "").Trim(), bottomLimit);

                    if (string.IsNullOrWhiteSpace(top) && string.IsNullOrWhiteSpace(bottom))
                        return "";

                    if (string.IsNullOrWhiteSpace(bottom))
                        return $"- Node {n.Id}\nTop: {top}".Trim();

                    return $"- Node {n.Id}\nTop: {top}\nBottom: {bottom}".Trim();
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (list.Count == 0)
                return "";

            return string.Join("\n\n", list);
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

        private IReadOnlyList<AiAttachment> CollectAiAttachments(NodeControl node)
        {
            var root = _main.GetAttachmentsRootDir();

            return _main.GetAttachmentsForNode(node)
                .Select(a => new AiAttachment
                {
                    FileName = a.FileName,
                    RelativePath = a.RelativePath,
                    AbsolutePath = Path.Combine(root, a.RelativePath),
                    MimeType = a.MimeType,
                    Kind = a.Kind
                })
                .Where(a => !string.IsNullOrWhiteSpace(a.AbsolutePath) && File.Exists(a.AbsolutePath))
                .ToList();
        }

        private Task<AiRequest> BuildAiRequestAsync(
            NodeControl currentNode,
            string model,
            string systemPrompt,
            string userPrompt,
            NodeTaskMode taskMode,
            bool useStreaming,
            int maxOutputTokens,
            CancellationToken ct)
        {
            var request = new AiRequest
            {
                ModelId = AiModelHelper.NormalizeNodeModel(model),
                SystemPrompt = systemPrompt ?? "",
                UserPrompt = userPrompt ?? "",
                TaskMode = taskMode,
                Attachments = CollectAiAttachments(currentNode),
                UseStreaming = useStreaming,
                MaxOutputTokens = maxOutputTokens,
                Metadata = new Dictionary<string, string>
                {
                    ["task_mode"] = NodeTaskModeHelper.ToStorageValue(taskMode)
                }
            };

            return Task.FromResult(request);
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
            if (maxChars <= 0) return "";
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "…";
        }
    }
}