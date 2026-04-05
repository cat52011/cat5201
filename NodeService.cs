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
        private readonly NodeModelSelectionService _modelSelection;
        private readonly NodeExecutionDecisionResolver _decisionResolver;
        private readonly NodeContextStrategyResolver _contextStrategyResolver;
        private readonly NodeContextService _contextService;
        private readonly NodePromptBuilder _promptBuilder;
        private readonly NodeExecutionCoreService _executionCoreService;
        private readonly NodeTranslationExecutionService _translationExecutionService;
        private readonly NodeExecutionHeuristicsService _executionHeuristics;

        private readonly NodeInstructionBuilder _instructionBuilder = new();
        private readonly NodeTextProcessingService _textProcessing = new();
        private readonly NodeExecutionLogFactory _executionLogFactory = new();

        private readonly NodeRequestFactory _requestFactory;
        private readonly NodeDecisionPresenter _decisionPresenter;
        private readonly NodeExecutionFinalizer _executionFinalizer;
        private const int AutoFlowMaxSteps = 12;

        public NodeService(AiServiceRouter router, MainWindow main)
        {
            _router = router;
            _main = main;
            _autoResolver = new AiAutoModelResolverService(router);

            _modelSelection = new NodeModelSelectionService();
            _contextStrategyResolver = new NodeContextStrategyResolver(router);
            _contextService = new NodeContextService(main);
            _promptBuilder = new NodePromptBuilder(_contextService);
            _executionHeuristics = new NodeExecutionHeuristicsService(main);

            _requestFactory = new NodeRequestFactory(main, router);
            _decisionPresenter = new NodeDecisionPresenter(main);
            _executionFinalizer = new NodeExecutionFinalizer(
                main,
                _decisionPresenter,
                _executionLogFactory);

            _decisionResolver = new NodeExecutionDecisionResolver(
                router,
                main,
                _autoResolver,
                _modelSelection);

            _executionCoreService = new NodeExecutionCoreService(
                _router,
                _contextStrategyResolver,
                _promptBuilder,
                _instructionBuilder,
                _textProcessing,
                BuildExecutionRequestAsync,
                ContinuationMaxRounds,
                MainReplyMaxOutputTokens);

            _translationExecutionService = new NodeTranslationExecutionService(
                _router,
                _instructionBuilder,
                _textProcessing,
                GenerateSinglePassOrContinuedAsync_Core,
                GenerateSinglePassOrContinuedStreamAsync_Core,
                BuildAiRequestAsync,
                SegmentDiscoveryMaxTokens,
                SegmentTranslationMaxTokens);
        }
        private const int MainReplyMaxOutputTokens = 8000;
        private const int ContinuationMaxRounds = 5;
        private const int SegmentDiscoveryMaxTokens = 1200;
        private const int SegmentTranslationMaxTokens = 8000;
        private NodeControl? _currentExecutionNode;
        private string? _currentExecutionInstructions;

        private sealed class SegmentPlanItem
        {
            public string Title { get; set; } = "";
            public string Hint { get; set; } = "";
        }

        private Task<AiRequest> BuildExecutionRequestAsync(
    string model,
    string prompt,
    int taskModeRaw,
    bool useStreaming,
    int maxOutputTokens,
    CancellationToken ct)
        {
            return BuildAiRequestAsync(
                _currentExecutionNode!,
                model,
                _currentExecutionInstructions!,
                prompt,
                (NodeTaskMode)taskModeRaw,
                useStreaming,
                maxOutputTokens,
                ct);
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

        public async Task<string> GenerateAsync(NodeControl node, string topText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return "";

            var startedAtUtc = DateTime.UtcNow;
            var decision = await _decisionResolver.ResolveAsync(node, topText, ct);
            _main.SetLiveDecisionResolving(node, decision);

            try
            {
                var execution = await ExecuteWithFallbackAsync(
                    node,
                    topText,
                    decision,
                    onDelta: null,
                    useStreaming: false,
                    ct);

                if (!execution.IsSuccess)
                {
                    FinalizeDecisionAfterExecution(decision, execution);
                    ApplyDecisionVisualization(decision);
                    _main.SetLiveDecisionFailed(node, decision, execution.ErrorMessage);
                    _main.ClearLiveDecisionState(node);
                    CommitExecutionLog(node, decision, startedAtUtc, success: false, errorMessage: execution.ErrorMessage);
                    throw new InvalidOperationException(execution.ErrorMessage);
                }

                FinalizeDecisionAfterExecution(decision, execution);
                ApplyDecisionVisualization(decision);
                SyncActualModelToNode(node, decision);
                _main.ClearLiveDecisionState(node);
                CommitExecutionLog(node, decision, startedAtUtc, success: true);

                var flowContext = new AutoFlowRunContext();
                flowContext.VisitedNodeIds.Add(node.Id);
                flowContext.StepCount = 1;

                await TryAutoFlowToNextNodeAsync(node, flowContext, ct);

                return execution.Text;
            }
            catch (Exception ex)
            {
                _main.SetLiveDecisionFailed(node, decision, ex.Message);
                _main.ClearLiveDecisionState(node);
                CommitExecutionLog(node, decision, startedAtUtc, success: false, errorMessage: ex.Message);
                throw;
            }
        }

        public async Task<string> GenerateStreamAsync(
    NodeControl node,
    string topText,
    Action<string> onDelta,
    CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return "";

            var startedAtUtc = DateTime.UtcNow;
            var decision = await _decisionResolver.ResolveAsync(node, topText, ct);
            _main.SetLiveDecisionResolving(node, decision);

            try
            {
                if (!decision.UseStreaming)
                {
                    var nonStreamingExecution = await ExecuteWithFallbackAsync(
                        node,
                        topText,
                        decision,
                        onDelta: null,
                        useStreaming: false,
                        ct);

                    if (!nonStreamingExecution.IsSuccess)
                    {
                        FinalizeDecisionAfterExecution(decision, nonStreamingExecution);
                        ApplyDecisionVisualization(decision);
                        _main.SetLiveDecisionFailed(node, decision, nonStreamingExecution.ErrorMessage);
                        _main.ClearLiveDecisionState(node);
                        CommitExecutionLog(node, decision, startedAtUtc, success: false, errorMessage: nonStreamingExecution.ErrorMessage);
                        throw new InvalidOperationException(nonStreamingExecution.ErrorMessage);
                    }

                    FinalizeDecisionAfterExecution(decision, nonStreamingExecution);
                    ApplyDecisionVisualization(decision);
                    SyncActualModelToNode(node, decision);
                    _main.ClearLiveDecisionState(node);
                    CommitExecutionLog(node, decision, startedAtUtc, success: true);

                    if (!string.IsNullOrWhiteSpace(nonStreamingExecution.Text))
                        onDelta?.Invoke(nonStreamingExecution.Text);

                    return nonStreamingExecution.Text;
                }

                var streamingExecution = await ExecuteWithFallbackAsync(
                    node,
                    topText,
                    decision,
                    onDelta,
                    useStreaming: true,
                    ct);

                if (!streamingExecution.IsSuccess)
                {
                    FinalizeDecisionAfterExecution(decision, streamingExecution);
                    ApplyDecisionVisualization(decision);
                    _main.SetLiveDecisionFailed(node, decision, streamingExecution.ErrorMessage);
                    _main.ClearLiveDecisionState(node);
                    CommitExecutionLog(node, decision, startedAtUtc, success: false, errorMessage: streamingExecution.ErrorMessage);
                    throw new InvalidOperationException(streamingExecution.ErrorMessage);
                }

                FinalizeDecisionAfterExecution(decision, streamingExecution);
                ApplyDecisionVisualization(decision);
                SyncActualModelToNode(node, decision);
                _main.ClearLiveDecisionState(node);
                CommitExecutionLog(node, decision, startedAtUtc, success: true);

                var flowContext = new AutoFlowRunContext();
                flowContext.VisitedNodeIds.Add(node.Id);
                flowContext.StepCount = 1;

                await TryAutoFlowToNextNodeAsync(node, flowContext, ct);

                return streamingExecution.Text;
            }
            catch (Exception ex)
            {
                _main.SetLiveDecisionFailed(node, decision, ex.Message);
                _main.ClearLiveDecisionState(node);
                CommitExecutionLog(node, decision, startedAtUtc, success: false, errorMessage: ex.Message);
                throw;
            }
        }

        private async Task<AiFallbackExecutionResult> ExecuteWithFallbackAsync(
    NodeControl node,
    string topText,
    NodeExecutionDecision decision,
    Action<string>? onDelta,
    bool useStreaming,
    CancellationToken ct)
        {
            var candidates = AiFallbackPlanner.BuildCandidates(decision.ModelId, decision.TaskMode);

            var attempts = new List<AiFallbackAttempt>();
            int emittedChars = 0;

            Action<string>? countingDelta = null;
            if (onDelta != null)
            {
                countingDelta = delta =>
                {
                    if (!string.IsNullOrEmpty(delta))
                        emittedChars += delta.Length;

                    onDelta(delta);
                };
            }

            for (int i = 0; i < candidates.Count; i++)
            {
                ct.ThrowIfCancellationRequested();

                var candidate = candidates[i];
                string candidateModel = AiModelHelper.NormalizeNodeModel(candidate.ModelId);
                bool isFallbackAttempt = i > 0 ||
    !string.Equals(candidateModel, decision.ModelId, StringComparison.OrdinalIgnoreCase);

                _main.SetLiveDecisionExecuting(
                    node,
                    decision,
                    candidateModel,
                    isFallbackAttempt,
                    i + 1,
                    candidate.Reason);
                try
                {
                    string text = await TryExecuteOnceAsync(
                        node,
                        topText,
                        candidateModel,
                        decision.TaskMode,
                        useStreaming ? countingDelta : null,
                        useStreaming && decision.UseStreaming && i == 0,
                        ct);

                    attempts.Add(new AiFallbackAttempt
                    {
                        AttemptIndex = i + 1,
                        ModelId = candidateModel,
                        Reason = candidate.Reason,
                        Success = true,
                        ErrorMessage = ""
                    });

                    bool usedFallback =
                        attempts.Count > 1 ||
                        !string.Equals(candidateModel, decision.ModelId, StringComparison.OrdinalIgnoreCase);

                    string summary = usedFallback
                        ? $"fallback 成功：{GetRuntimeModelLabel(candidateModel)}"
                        : "";

                    return new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = text ?? "",
                        ActualModelId = candidateModel,
                        UsedFallback = usedFallback,
                        Summary = summary,
                        ErrorMessage = "",
                        Attempts = attempts
                    };
                }
                catch (Exception ex)
                {
                    attempts.Add(new AiFallbackAttempt
                    {
                        AttemptIndex = i + 1,
                        ModelId = candidateModel,
                        Reason = candidate.Reason,
                        Success = false,
                        ErrorMessage = ex.Message
                    });

                    // 若第一個串流嘗試已經有輸出，後面不能安全 fallback，避免混入兩個模型的內容
                    if (useStreaming && emittedChars > 0)
                    {
                        return new AiFallbackExecutionResult
                        {
                            IsSuccess = false,
                            Text = "",
                            ActualModelId = candidateModel,
                            UsedFallback = false,
                            Summary = "串流過程中斷，已停止後續 fallback",
                            ErrorMessage = ex.Message,
                            Attempts = attempts
                        };
                    }
                }
            }

            string lastError = attempts.Count > 0
                ? attempts[attempts.Count - 1].ErrorMessage
                : "未知錯誤";

            return new AiFallbackExecutionResult
            {
                IsSuccess = false,
                Text = "",
                ActualModelId = decision.ModelId,
                UsedFallback = attempts.Count > 1,
                Summary = attempts.Count > 1 ? "所有 fallback 皆失敗" : "",
                ErrorMessage = lastError,
                Attempts = attempts
            };
        }

        private async Task<string> TryExecuteOnceAsync(
    NodeControl node,
    string topText,
    string model,
    NodeTaskMode taskMode,
    Action<string>? onDelta,
    bool useStreaming,
    CancellationToken ct)
        {
            bool useSegmentMode =
    taskMode == NodeTaskMode.Translate &&
    _executionHeuristics.LooksLikeFullTranslationRequest(topText) &&
    _executionHeuristics.HasNonImageAttachments(node);

            if (useSegmentMode)
            {
                if (useStreaming && onDelta != null)
                {
                    return await _translationExecutionService.TranslateStreamAsync(
                        node,
                        topText,
                        model,
                        taskMode,
                        onDelta,
                        ct);
                }

                return await _translationExecutionService.TranslateAsync(
                    node,
                    topText,
                    model,
                    taskMode,
                    ct);
            }

            if (useStreaming && onDelta != null)
            {
                return await GenerateSinglePassOrContinuedStreamAsync_Core(
                    node,
                    topText,
                    model,
                    taskMode,
                    onDelta,
                    ct);
            }

            return await GenerateSinglePassOrContinuedAsync_Core(
                node,
                topText,
                model,
                taskMode,
                ct);
        }



        private async Task<string> GenerateSinglePassOrContinuedAsync_Core(
    NodeControl currentNode,
    string topText,
    string model,
    NodeTaskMode taskMode,
    CancellationToken ct)
        {
            _currentExecutionNode = currentNode;

            var route = _router.GetRouteInfo(model);
            _currentExecutionInstructions = route.Provider == AiProviderKind.PerplexitySonar
                ? _instructionBuilder.BuildPerplexityInstructions(model, route.IsDeepResearch, taskMode)
                : _instructionBuilder.BuildGeneralNodeInstructions(model, taskMode);

            try
            {
                return await _executionCoreService.ExecuteAsync(
                    currentNode,
                    topText,
                    model,
                    taskMode,
                    ct);
            }
            finally
            {
                _currentExecutionNode = null;
                _currentExecutionInstructions = null;
            }
        }
        private async Task<string> GenerateSinglePassOrContinuedStreamAsync_Core(
    NodeControl currentNode,
    string topText,
    string model,
    NodeTaskMode taskMode,
    Action<string> onDelta,
    CancellationToken ct)
        {
            _currentExecutionNode = currentNode;

            var route = _router.GetRouteInfo(model);
            _currentExecutionInstructions = route.Provider == AiProviderKind.PerplexitySonar
                ? _instructionBuilder.BuildPerplexityInstructions(model, route.IsDeepResearch, taskMode)
                : _instructionBuilder.BuildGeneralNodeInstructions(model, taskMode);

            try
            {
                return await _executionCoreService.ExecuteStreamAsync(
                    currentNode,
                    topText,
                    model,
                    taskMode,
                    onDelta,
                    ct);
            }
            finally
            {
                _currentExecutionNode = null;
                _currentExecutionInstructions = null;
            }
        }

        private Task<AiRequest> BuildAiRequestInternalAsync(
    NodeControl currentNode,
    string model,
    string instructions,
    string userPrompt,
    NodeTaskMode taskMode,
    bool useStreaming,
    int maxOutputTokens,
    CancellationToken ct)
        {
            return _requestFactory.BuildAsync(
                currentNode,
                model,
                instructions,
                userPrompt,
                taskMode,
                useStreaming,
                maxOutputTokens,
                ct);
        }

        private NodeContextStrategy GetContextStrategy(string model, NodeTaskMode taskMode)
        {
            return _contextStrategyResolver.Resolve(model, taskMode);
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

        private IEnumerable<MainWindow.ContextConnectionInfo> GetIncoming(NodeControl node)
            => GetConnections().Where(c => ReferenceEquals(c.EndNode, node));

        private IEnumerable<MainWindow.ContextConnectionInfo> GetOutgoing(NodeControl node)
            => GetConnections().Where(c => ReferenceEquals(c.StartNode, node));

        private IEnumerable<MainWindow.ContextConnectionInfo> GetConnections()
        {
            foreach (var c in _main.GetAllConnections())
            {
                yield return new MainWindow.ContextConnectionInfo
                {
                    StartNode = c.StartNode,
                    StartThumb = c.StartThumb,
                    EndNode = c.EndNode,
                    EndThumb = c.EndThumb
                };
            }
        }

        private static string ToDataUrl(byte[] bytes, string mime)
        {
            var b64 = Convert.ToBase64String(bytes);
            return $"data:{mime};base64,{b64}";
        }

        private static string NormalizeForSimilarity(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var s = text.Replace("\r\n", "\n").Replace('\r', '\n');
            s = Regex.Replace(s, @"\s+", " ");
            s = s.Trim().ToLowerInvariant();
            return s;
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

        private NodeExecutionDecision FinalizeDecisionAfterExecution(
    NodeExecutionDecision decision,
    AiFallbackExecutionResult execution)
        {
            return _executionFinalizer.FinalizeDecision(decision, execution);
        }

        private void ApplyDecisionVisualization(NodeExecutionDecision decision)
        {
            _executionFinalizer.Present(decision);
        }

        private void SyncActualModelToNode(NodeControl node, NodeExecutionDecision decision)
        {
            _executionFinalizer.SyncActualModelToNode(node, decision);
        }

        private void CommitExecutionLog(
            NodeControl node,
            NodeExecutionDecision decision,
            DateTime startedAtUtc,
            bool success,
            string errorMessage = "")
        {
            _executionFinalizer.CommitExecutionLog(
                node,
                decision,
                startedAtUtc,
                success,
                errorMessage);
        }

        private AiRouteInfo PrepareRoute(string? selectedModel)
        {
            var route = _router.GetRouteInfo(selectedModel);
            _router.EnsureServiceReady(route);
            return route;
        }
        private async Task TryAutoFlowToNextNodeAsync(
    NodeControl currentNode,
    AutoFlowRunContext flowContext,
    CancellationToken ct)
        {
            if (currentNode == null || flowContext == null)
                return;

            if (flowContext.StepCount >= AutoFlowMaxSteps)
                return;

            var nextNode = _main.GetFirstDownstreamNode(currentNode);
            if (nextNode == null)
                return;

            // 防自己連自己
            if (ReferenceEquals(nextNode, currentNode))
                return;

            // 防循環 / 同輪重複執行
            if (flowContext.VisitedNodeIds.Contains(nextNode.Id))
                return;

            if (!_main.NodeAcceptsAutoFlowInput(nextNode))
                return;

            bool prepared = _main.TryPrepareAutoFlowInput(currentNode, nextNode);
            if (!prepared)
                return;

            _main.FocusDecisionNode(nextNode);

            string nextTopText = nextNode.GetTopText() ?? "";
            if (string.IsNullOrWhiteSpace(nextTopText))
                return;

            nextNode.ClearBottomText();
            nextNode.EndEditBecauseSent();

            flowContext.VisitedNodeIds.Add(nextNode.Id);
            flowContext.StepCount++;

            string finalText = await GenerateStreamAsync(
                nextNode,
                nextTopText,
                delta =>
                {
                    nextNode.Dispatcher.Invoke(() =>
                    {
                        nextNode.AppendBottomText(delta);
                    });
                },
                ct);

            if (string.IsNullOrWhiteSpace(finalText))
                return;
        }

        private sealed class AutoFlowRunContext
        {
            public HashSet<Guid> VisitedNodeIds { get; } = new();
            public int StepCount { get; set; }
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