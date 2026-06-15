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
        private readonly AgentRuntimeFactory _agentRuntimeFactory;

        // 只有當圖片本身就是這次的主要產出（純圖片生成）時，才在輸出區直接顯示大圖。
        // 若同一次還產出了簡報 / 報告（pptx / md / docx），那張圖只是封面配圖，
        // 已嵌入 deck 並以可點擊 chip 列出，輸出區不再重複丟一張大圖。
        private static string? ResolveInlineOutputImage(AgentWorkspace workspace)
        {
            if (workspace == null)
                return null;

            var generatedFiles = workspace.GetByType("generated_file")
                .Select(x => x.Payload)
                .OfType<GeneratedFilePayload>()
                .Where(f => f != null && f.Success)
                .ToList();

            if (generatedFiles.Count == 0)
                return null;

            bool hasNonImageDeliverable = generatedFiles.Any(f =>
                !string.Equals(f.Format, "image", StringComparison.OrdinalIgnoreCase));

            if (hasNonImageDeliverable)
                return null;

            return generatedFiles
                .Where(f => string.Equals(f.Format, "image", StringComparison.OrdinalIgnoreCase))
                .Select(f => f.FilePath)
                .FirstOrDefault();
        }

        public NodeService(AiServiceRouter router, MainWindow main)
        {
            _router = router;
            _main = main;
            _autoResolver = new AiAutoModelResolverService(router);

            _modelSelection = new NodeModelSelectionService();
            // 個人化自訂路由：與 MainWindow 共用同一個 overrides 物件，讓執行路徑與 UI 預覽一致。
            _modelSelection.UseOverrides(main.TaskRoutingOverrides);
            _contextStrategyResolver = new NodeContextStrategyResolver(router);
            _contextService = new NodeContextService(main);
            _memoryStore = new MemoryStore(@"D:\desk\college\final\file");
            _memoryService = new NodeMemoryService(main, _memoryStore);
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
            _agentRuntimeFactory = new AgentRuntimeFactory(
    main,
    _decisionResolver,
    _executionFinalizer,
    ExecuteWithFallbackAsync);

            _executionCoreService = new NodeExecutionCoreService(
    _router,
    _contextStrategyResolver,
    _promptBuilder,
    _instructionBuilder,
    _textProcessing,
    _memoryService,
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
            InitializeAgentCapabilities();
        }


        private const int MainReplyMaxOutputTokens = 8000;
        private const int ContinuationMaxRounds = 5;
        private const int SegmentDiscoveryMaxTokens = 1200;
        private const int SegmentTranslationMaxTokens = 8000;
        private NodeControl? _currentExecutionNode;
        private string? _currentExecutionInstructions;
        private readonly MemoryStore _memoryStore;
        private readonly NodeMemoryService _memoryService;
        private readonly WorkflowRunStore _workflowRuns = new();

        // ===== Memory v1：給 MainWindow 側邊欄記憶/偏好面板用的公開 API =====

        /// <summary>手動輸入偏好/記憶指令；回傳擷取到的偏好顯示值。</summary>
        public IReadOnlyList<string> AddManualPreference(string text)
            => _memoryService.CaptureExplicitPreference(text);

        /// <summary>目前所有偏好的顯示清單。</summary>
        public IReadOnlyList<string> GetPreferenceList()
            => _memoryService.GetPreferenceDisplayList();

        /// <summary>個人化清單：整句顯示 + 可刪除 key。</summary>
        public IReadOnlyList<PreferenceView> GetPreferenceItems()
            => _memoryService.GetPreferenceItems();

        public int DeletePreference(string key) => _memoryService.DeletePreference(key);

        /// <summary>(偏好筆數, episodic 筆數)。</summary>
        public (int preferences, int episodic) GetMemoryStats()
            => _memoryService.GetMemoryStats();

        public int ClearAllMemory() => _memoryService.ClearAllMemory();
        public int ClearPreferenceMemory() => _memoryService.ClearPreferences();
        public int ClearEpisodicMemory() => _memoryService.ClearEpisodicMemory();

        /// <summary>執行後在 UI 執行緒刷新側邊欄記憶面板（被動偏好/記憶數可能已變動）。</summary>
        private void TryRefreshMemoryPanel()
        {
            try { _main.Dispatcher.Invoke(() => _main.RefreshMemoryPanel()); }
            catch { }
        }

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

        private static string SanitizeNodeFinalText(string topText, string text)
        {
            return FinalAnswerSanitizer.Sanitize(
                text ?? "",
                enforceSynthesisFormat: IsFinanceLikeTask(topText));
        }

        private static bool IsFinanceLikeTask(string text)
        {
            return FinanceTaskDetector.IsFinanceLike(text);
        }

        private bool ShouldSkipCapabilities(string topText)
        {
            if (_main.IsAdvancedAutoResolverEnabled())
                return false;

            return !IsFinanceLikeTask(topText);
        }

        public async Task<string> GenerateAsync(NodeControl node, string topText, CancellationToken ct)
        {
            if (_main.TryBuildInputFromFirstUpstream(node, out var injectedTopText))
                topText = injectedTopText;

            if (string.IsNullOrWhiteSpace(topText))
                return "";

            var startedAtUtc = DateTime.UtcNow;
            var agent = AgentRegistry.Get(_main.GetNodeSelectedAgent(node));
            var runtime = _agentRuntimeFactory.Create();

            try
            {
                var workspace = new AgentWorkspace();

                var agentResult = await runtime.ExecuteAsync(new AgentExecutionRequest
                {
                    Node = node,
                    Agent = agent,
                    TopText = topText,
                    UseStreaming = false,
                    OnDelta = null,
                    CancellationToken = ct,
                    SkipCapabilities = ShouldSkipCapabilities(topText),
                    Workspace = workspace,
                    PreferenceBlock = _memoryService.GetPreferenceBlock()
                });

                var decision = agentResult.Decision;
                var execution = agentResult.Execution;

                // Memory v1 視覺化：記錄本次召回的偏好 / 記憶，供 decision-viz 顯示。
                decision.MemoryRecall = _memoryService.GetRecallStats(
                    node,
                    string.IsNullOrWhiteSpace(decision.ActualAgentId) ? agent.Id : decision.ActualAgentId,
                    topText,
                    decision.TaskMode);

                if (!execution.IsSuccess)
                {
                    ApplyDecisionVisualization(decision);
                    _main.SetLiveDecisionFailed(node, decision, execution.ErrorMessage);
                    _main.ClearLiveDecisionState(node);
                    CommitExecutionLog(node, decision, startedAtUtc, success: false, errorMessage: execution.ErrorMessage);
                    throw new InvalidOperationException(execution.ErrorMessage);
                }

                ApplyDecisionVisualization(decision);
                SyncActualModelToNode(node, decision);
                _main.ClearLiveDecisionState(node);
                CommitExecutionLog(node, decision, startedAtUtc, success: true);

                // 把本次產生、可開啟的檔案推給節點，顯示成可點擊的 chip。
                node.SetOutputFiles(workspace.GetByType("generated_file")
                    .Select(x => x.Payload)
                    .OfType<GeneratedFilePayload>()
                    .Where(f => f != null && f.Success)
                    .ToList());

                node.SetOutputImage(ResolveInlineOutputImage(workspace));

                await _memoryService.RememberExecutionResultAsync(
    node,
    decision.ActualAgentId,
    topText,
    execution.Text,
    decision.TaskMode,
    decision.ActualModelId,
    ct);

                await _memoryService.RememberCapabilityTraceAsync(
                    node,
                    decision.ActualAgentId,
                    topText,
                    decision.CapabilityTrace,
                    decision.TaskMode,
                    decision.ActualModelId,
                    ct);

                await _memoryService.RememberDelegationTraceAsync(
                    node,
                    decision.ActualAgentId,
                    topText,
                    decision.DelegationTrace,
                    decision.TaskMode,
                    decision.ActualModelId,
                    ct);

                TryRefreshMemoryPanel();

                if (agentResult.WorkspaceSummary != null)
                {
                    await _memoryService.RememberWorkspaceSummaryAsync(
                        node,
                        decision.ActualAgentId,
                        topText,
                        agentResult.WorkspaceSummary,
                        decision.TaskMode,
                        decision.ActualModelId,
                        ct);
                }

                // §3：記錄本次工作流執行，並快取成功的 workspace 供「重新生成答案」沿用。
                RecordWorkflowRun(node, workspace, topText, WorkflowRunKind.Initial, success: true,
                    outputPreview: execution.Text, errorMessage: "", startedAtUtc: startedAtUtc);

                var flowContext = new AutoFlowRunContext();
                flowContext.VisitedNodeIds.Add(node.Id);
                flowContext.StepCount = 1;

                await TryAutoFlowToNextNodeAsync(node, flowContext, ct);

                return SanitizeNodeFinalText(topText, execution.Text);
            }
            catch (Exception ex)
            {
                var failedDecision = new NodeExecutionDecision
                {
                    RequestedAgentId = agent.Id,
                    ActualAgentId = agent.Id,
                    ResolverLabel = "AgentRuntime",
                    StatusLabel = _main.IsAutoModelSelectionEnabled() ? "Auto" : "Manual"
                };

                _main.SetLiveDecisionFailed(node, failedDecision, ex.Message);
                _main.ClearLiveDecisionState(node);
                CommitExecutionLog(node, failedDecision, startedAtUtc, success: false, errorMessage: ex.Message);
                throw;
            }
        }

        public Task<string> GenerateStreamAsync(
            NodeControl node,
            string topText,
            Action<string> onDelta,
            CancellationToken ct)
        {
            if (_main.TryBuildInputFromFirstUpstream(node, out var injectedTopText))
                topText = injectedTopText;

            if (string.IsNullOrWhiteSpace(topText))
                return Task.FromResult("");

            return ExecuteNodeStreamAsync(
                node,
                topText,
                onDelta,
                ct,
                reuseWorkspace: null,
                skipCapabilitiesOverride: null,
                runKind: WorkflowRunKind.Initial);
        }

        // ===== §3 Workflow replay / rerun / resume：對外操作 =====

        /// <summary>用上一次的相同輸入整段重播；若上一次失敗則語意上等同「從失敗續跑」。</summary>
        public Task<string> ReplayWorkflowStreamAsync(
            NodeControl node,
            Action<string> onDelta,
            CancellationToken ct)
        {
            var last = _workflowRuns.GetLast(node.Id);
            string input = last?.OriginalInput ?? "";

            if (string.IsNullOrWhiteSpace(input) &&
                _main.TryBuildInputFromFirstUpstream(node, out var injected))
            {
                input = injected;
            }

            if (string.IsNullOrWhiteSpace(input))
                return Task.FromResult("");

            var kind = (last != null && !last.Success)
                ? WorkflowRunKind.Resume
                : WorkflowRunKind.Replay;

            return ExecuteNodeStreamAsync(
                node,
                input,
                onDelta,
                ct,
                reuseWorkspace: null,
                skipCapabilitiesOverride: null,
                runKind: kind);
        }

        /// <summary>
        /// 只重新生成最終答案：沿用上一次成功的 workspace（research / capability 成果），
        /// 跳過 capability 層，因此不會重跑昂貴的搜尋/分析，只重做 final synthesis。
        /// 沒有快取可用時退回整段重播。
        /// </summary>
        public Task<string> RegenerateAnswerStreamAsync(
            NodeControl node,
            Action<string> onDelta,
            CancellationToken ct)
        {
            if (_workflowRuns.TryGetCachedWorkspace(node.Id, out var ws, out var input) &&
                ws != null &&
                !string.IsNullOrWhiteSpace(input))
            {
                return ExecuteNodeStreamAsync(
                    node,
                    input,
                    onDelta,
                    ct,
                    reuseWorkspace: ws,
                    skipCapabilitiesOverride: true,
                    runKind: WorkflowRunKind.RegenerateAnswer);
            }

            return ReplayWorkflowStreamAsync(node, onDelta, ct);
        }

        public IReadOnlyList<WorkflowRunRecord> GetWorkflowRuns(NodeControl node)
            => node == null ? Array.Empty<WorkflowRunRecord>() : _workflowRuns.GetRuns(node.Id);

        public WorkflowRunRecord? GetLastWorkflowRun(NodeControl node)
            => node == null ? null : _workflowRuns.GetLast(node.Id);

        /// <summary>是否有可沿用的成功 workspace（決定「重新生成答案」是否能省略 research）。</summary>
        public bool CanRegenerateAnswer(NodeControl node)
            => node != null && _workflowRuns.HasCachedWorkspace(node.Id);

        private async Task<string> ExecuteNodeStreamAsync(
            NodeControl node,
            string topText,
            Action<string>? onDelta,
            CancellationToken ct,
            AgentWorkspace? reuseWorkspace,
            bool? skipCapabilitiesOverride,
            WorkflowRunKind runKind)
        {
            var startedAtUtc = DateTime.UtcNow;
            var agent = AgentRegistry.Get(_main.GetNodeSelectedAgent(node));
            var runtime = _agentRuntimeFactory.Create();
            var workspace = reuseWorkspace ?? new AgentWorkspace();
            bool skipCapabilities = skipCapabilitiesOverride ?? ShouldSkipCapabilities(topText);

            try
            {
                var agentResult = await runtime.ExecuteAsync(new AgentExecutionRequest
                {
                    Node = node,
                    Agent = agent,
                    TopText = topText,
                    UseStreaming = true,
                    OnDelta = onDelta,
                    CancellationToken = ct,
                    SkipCapabilities = skipCapabilities,
                    Workspace = workspace,
                    PreferenceBlock = _memoryService.GetPreferenceBlock()
                });

                var decision = agentResult.Decision;
                var execution = agentResult.Execution;

                // Memory v1 視覺化：記錄本次召回的偏好 / 記憶，供 decision-viz 顯示。
                decision.MemoryRecall = _memoryService.GetRecallStats(
                    node,
                    string.IsNullOrWhiteSpace(decision.ActualAgentId) ? agent.Id : decision.ActualAgentId,
                    topText,
                    decision.TaskMode);

                if (!execution.IsSuccess)
                {
                    ApplyDecisionVisualization(decision);
                    _main.SetLiveDecisionFailed(node, decision, execution.ErrorMessage);
                    _main.ClearLiveDecisionState(node);
                    CommitExecutionLog(node, decision, startedAtUtc, success: false, errorMessage: execution.ErrorMessage);
                    throw new InvalidOperationException(execution.ErrorMessage);
                }

                ApplyDecisionVisualization(decision);
                SyncActualModelToNode(node, decision);
                _main.ClearLiveDecisionState(node);
                CommitExecutionLog(node, decision, startedAtUtc, success: true);

                // 把本次產生、可開啟的檔案（報告 / 簡報 deck）推給節點，顯示成可點擊的 chip。
                node.SetOutputFiles(workspace.GetByType("generated_file")
                    .Select(x => x.Payload)
                    .OfType<GeneratedFilePayload>()
                    .Where(f => f != null && f.Success)
                    .ToList());

                node.SetOutputImage(ResolveInlineOutputImage(workspace));

                await _memoryService.RememberExecutionResultAsync(
    node,
    decision.ActualAgentId,
    topText,
    execution.Text,
    decision.TaskMode,
    decision.ActualModelId,
    ct);

                await _memoryService.RememberCapabilityTraceAsync(
                    node,
                    decision.ActualAgentId,
                    topText,
                    decision.CapabilityTrace,
                    decision.TaskMode,
                    decision.ActualModelId,
                    ct);

                await _memoryService.RememberDelegationTraceAsync(
                    node,
                    decision.ActualAgentId,
                    topText,
                    decision.DelegationTrace,
                    decision.TaskMode,
                    decision.ActualModelId,
                    ct);

                TryRefreshMemoryPanel();

                if (agentResult.WorkspaceSummary != null)
                {
                    await _memoryService.RememberWorkspaceSummaryAsync(
                        node,
                        decision.ActualAgentId,
                        topText,
                        agentResult.WorkspaceSummary,
                        decision.TaskMode,
                        decision.ActualModelId,
                        ct);
                }

                // §3：記錄本次工作流執行，並快取成功的 workspace 供「重新生成答案」沿用。
                RecordWorkflowRun(node, workspace, topText, runKind, success: true,
                    outputPreview: execution.Text, errorMessage: "", startedAtUtc: startedAtUtc);

                var flowContext = new AutoFlowRunContext();
                flowContext.VisitedNodeIds.Add(node.Id);
                flowContext.StepCount = 1;

                await TryAutoFlowToNextNodeAsync(node, flowContext, ct);

                return SanitizeNodeFinalText(topText, execution.Text);
            }
            catch (Exception ex)
            {
                var failedDecision = new NodeExecutionDecision
                {
                    RequestedAgentId = agent.Id,
                    ActualAgentId = agent.Id,
                    ResolverLabel = "AgentRuntime",
                    StatusLabel = _main.IsAutoModelSelectionEnabled() ? "Auto" : "Manual"
                };

                _main.SetLiveDecisionFailed(node, failedDecision, ex.Message);
                _main.ClearLiveDecisionState(node);
                CommitExecutionLog(node, failedDecision, startedAtUtc, success: false, errorMessage: ex.Message);

                // §3：失敗也記錄，讓「從失敗續跑」與執行 log 有依據。
                RecordWorkflowRun(node, workspace, topText, runKind, success: false,
                    outputPreview: "", errorMessage: ex.Message, startedAtUtc: startedAtUtc);

                throw;
            }
        }

        // 從 workspace 的 orchestration_plan 取各 step 狀態，建立一筆工作流執行紀錄。
        private void RecordWorkflowRun(
            NodeControl node,
            AgentWorkspace workspace,
            string originalInput,
            WorkflowRunKind kind,
            bool success,
            string outputPreview,
            string errorMessage,
            DateTime startedAtUtc)
        {
            try
            {
                var plan = workspace.GetByType("workflow")
                    .Select(x => x.Payload)
                    .OfType<OrchestrationPlanPayload>()
                    .FirstOrDefault();

                var steps = new List<WorkflowRunStep>();
                if (plan?.Stages != null)
                {
                    foreach (var s in plan.Stages)
                    {
                        if (s == null)
                            continue;

                        steps.Add(new WorkflowRunStep
                        {
                            Order = s.Order,
                            Id = s.Id,
                            Label = s.Label,
                            Status = s.Status,
                            Detail = s.Detail
                        });
                    }
                }

                string resumedFrom = kind == WorkflowRunKind.Resume
                    ? steps.FirstOrDefault(x => !IsStepTerminalSuccess(x.Status))?.Id ?? ""
                    : "";

                var record = new WorkflowRunRecord
                {
                    NodeId = node.Id,
                    Kind = kind,
                    StartedAtUtc = startedAtUtc,
                    FinishedAtUtc = DateTime.UtcNow,
                    OriginalInput = originalInput ?? "",
                    Success = success,
                    OverallStatus = plan?.Status ?? (success ? "success" : "failed"),
                    OutputPreview = Truncate(outputPreview ?? "", 400),
                    ErrorMessage = Truncate(errorMessage ?? "", 400),
                    ResumedFromStepId = resumedFrom,
                    Steps = steps
                };

                _workflowRuns.Record(node.Id, record);

                if (success)
                    _workflowRuns.CacheWorkspace(node.Id, workspace, originalInput ?? "");
            }
            catch
            {
                // 記錄失敗不可影響主流程。
            }
        }

        private static bool IsStepTerminalSuccess(string status)
            => string.Equals(status, "success", StringComparison.OrdinalIgnoreCase);

        private void InitializeAgentCapabilities()
        {
            AgentCapabilityRegistry.Clear();

            AgentCapabilityRegistry.Register(
                new TaskPlanningCapability());

            AgentCapabilityRegistry.Register(
                new SearchCapability(_main.GetPerplexityToolService()));

            AgentCapabilityRegistry.Register(
                new FileCapability());

            AgentCapabilityRegistry.Register(
                new CodeCapability());

            AgentCapabilityRegistry.Register(
                new ReasoningCapability());

            AgentCapabilityRegistry.Register(
                new ImageCapability());
        }

        private async Task<AiFallbackExecutionResult> ExecuteWithFallbackAsync(
    NodeControl node,
    string topText,
    NodeExecutionDecision decision,
    Action<string>? onDelta,
    bool useStreaming,
    CancellationToken ct)
        {
            // §15 個人化：fallback 鏈的成本過濾只在 Auto / API 模式套用個人化開關；
            // 手動模式永遠獨立——使用者選什麼就以它為主，不做任何成本剔除。
            bool applyUserCostBlock = _main.IsAutoModelSelectionEnabled();

            var candidates = AiFallbackPlanner.BuildCandidates(
                decision.ModelId,
                decision.TaskMode,
                applyUserCostBlock);

            if (decision.ForceSingleModel)
            {
                var forcedModel = AiModelHelper.NormalizeNodeModel(decision.ModelId);

                candidates = candidates
                    .Where(x => string.Equals(
                        AiModelHelper.NormalizeNodeModel(x.ModelId),
                        forcedModel,
                        StringComparison.OrdinalIgnoreCase))
                    .Take(1)
                    .ToList();

                if (candidates.Count == 0)
                {
                    candidates = AiFallbackPlanner.BuildCandidates(
                            decision.ModelId,
                            decision.TaskMode,
                            applyUserCostBlock)
                        .Take(1)
                        .ToList();
                }
            }
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

                    return FinalAnswerSanitizer.Sanitize(new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = text ?? "",
                        ActualModelId = candidateModel,
                        UsedFallback = usedFallback,
                        Summary = summary,
                        ErrorMessage = "",
                        Attempts = attempts
                    }, enforceSynthesisFormat: false);
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
                string agentId = _main.GetNodeSelectedAgent(currentNode);

                return await _executionCoreService.ExecuteAsync(
                    currentNode,
                    agentId,
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
                string agentId = _main.GetNodeSelectedAgent(currentNode);

                return await _executionCoreService.ExecuteStreamAsync(
                    currentNode,
                    agentId,
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
                    MimeType = NormalizeAttachmentMimeType(a.FileName, a.MimeType),
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

            var downstreamNodes = _main.GetDownstreamNodes(currentNode);
            if (downstreamNodes == null || downstreamNodes.Count == 0)
                return;

            foreach (var nextNode in downstreamNodes)
            {
                ct.ThrowIfCancellationRequested();

                if (nextNode == null)
                    continue;

                if (flowContext.StepCount >= AutoFlowMaxSteps)
                    return;

                // 防自己連自己
                if (ReferenceEquals(nextNode, currentNode))
                    continue;

                // 防循環 / 同輪重複執行
                if (flowContext.VisitedNodeIds.Contains(nextNode.Id))
                    continue;

                // 節點 policy：未啟用 auto run 就跳過
                if (!_main.IsNodeAutoRunEnabled(nextNode))
                    continue;

                if (!_main.NodeAcceptsAutoFlowInput(nextNode))
                    continue;

                bool prepared = _main.TryPrepareAutoFlowInput(currentNode, nextNode);
                if (!prepared)
                    continue;

                _main.FocusDecisionNode(nextNode);

                string nextTopText = nextNode.GetTopText() ?? "";
                if (string.IsNullOrWhiteSpace(nextTopText))
                    continue;

                nextNode.ClearBottomText();
                nextNode.EndEditBecauseSent();

                flowContext.VisitedNodeIds.Add(nextNode.Id);
                flowContext.StepCount++;

                try
                {
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
                        continue;
                }
                catch
                {
                    var policy = _main.GetNodeAutoFlowPolicy(nextNode);
                    if (policy.StopFlowOnError)
                        return;
                }
            }
        }

        private static string NormalizeAttachmentMimeType(string? fileName, string? mimeType)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();

            return ext switch
            {
                ".cs" => "text/plain",
                ".xaml" => "text/plain",
                ".java" => "text/plain",
                ".cpp" => "text/plain",
                ".h" => "text/plain",
                ".hpp" => "text/plain",
                ".py" => "text/plain",
                ".js" => "text/plain",
                ".ts" => "text/plain",
                ".json" => "application/json",
                ".csv" => "text/csv",
                ".txt" => "text/plain",
                ".md" => "text/markdown",
                ".pdf" => "application/pdf",
                _ => string.IsNullOrWhiteSpace(mimeType)
                    ? "application/octet-stream"
                    : mimeType
            };
        }
        private sealed class AutoFlowRunContext
        {
            public HashSet<Guid> VisitedNodeIds { get; } = new();
            public int StepCount { get; set; }

            public Guid RunId { get; } = Guid.NewGuid();

            public List<string> Trace { get; } = new();
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
