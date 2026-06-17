using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class AgentRuntime
    {
        private const int MaxCodeSnapshotPromptCharsTotal = 24000;
        private const int MaxCodeSnapshotPromptCharsPerFile = 18000;
        private const int MaxCodeSourceOutlineCharsPerFile = 2500;
        private const int MaxRepairPreviousOutputChars = 2500;
        private const int MaxRepairDiffChars = 14000;

        private readonly MainWindow _main;
        private readonly NodeExecutionDecisionResolver _decisionResolver;
        private readonly Func<NodeControl, string, NodeExecutionDecision, Action<string>?, bool, CancellationToken, Task<AiFallbackExecutionResult>> _executeWithFallbackAsync;
        private readonly NodeExecutionFinalizer _executionFinalizer;
        private readonly AgentDelegationPlanner _delegationPlanner = new();

        public AgentRuntime(
            MainWindow main,
            NodeExecutionDecisionResolver decisionResolver,
            Func<NodeControl, string, NodeExecutionDecision, Action<string>?, bool, CancellationToken, Task<AiFallbackExecutionResult>> executeWithFallbackAsync,
            NodeExecutionFinalizer executionFinalizer)
        {
            _main = main;
            _decisionResolver = decisionResolver;
            _executeWithFallbackAsync = executeWithFallbackAsync;
            _executionFinalizer = executionFinalizer;
        }

        public async Task<AgentExecutionResult> ExecuteAsync(
            AgentExecutionRequest request)
        {
            if (request == null)
                throw new ArgumentNullException(nameof(request));

            if (request.Agent == null)
                throw new InvalidOperationException("AgentExecutionRequest.Agent 不可為 null。");

            string topText = request.TopText ?? "";
            if (request.Workspace == null)
            {
                throw new InvalidOperationException(
                    "AgentExecutionRequest.Workspace 不可為 null。Parallel / Delegate flow 必須共享同一個 workspace instance。");
            }

            var workspace = request.Workspace;
            var node = request.Node ?? throw new InvalidOperationException("AgentExecutionRequest.Node 不可為 null。");
            var parallelRunner = new AgentParallelRunner(ExecuteAsync);
            if (string.IsNullOrWhiteSpace(topText))
            {
                return new AgentExecutionResult
                {
                    Decision = new NodeExecutionDecision
                    {
                        RequestedAgentId = request.Agent.Id,
                        ActualAgentId = request.Agent.Id,
                        ResolverLabel = "AgentRuntime",
                        StatusLabel = _main.IsAutoModelSelectionEnabled() ? "Auto" : "Manual"
                    },
                    Execution = new AiFallbackExecutionResult
                    {
                        IsSuccess = true,
                        Text = "",
                        ActualModelId = request.Agent.DefaultModelId
                    }
                };
            }

            var capabilityTrace = new List<AgentCapabilityTraceItem>();
            var delegationTrace = new List<AgentDelegationTraceItem>();
            var capabilityData = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            // 1. resolve decision
            var decision = await _decisionResolver.ResolveAsync(
                node,
                topText,
                request.CancellationToken);

            if (string.IsNullOrWhiteSpace(decision.RequestedAgentId))
                decision.RequestedAgentId = request.Agent.Id;

            if (string.IsNullOrWhiteSpace(decision.ActualAgentId))
                decision.ActualAgentId = request.Agent.Id;

            if (request.ForceAgentProfile)
            {
                var forcedAgent = request.Agent;

                var profile = _agentProfileResolver.Resolve(
                    forcedAgent,
                    preferredModelId: forcedAgent.DefaultModelId,
                    preferredTaskMode: decision.TaskMode);

                string normalizedRuntime = AiModelHelper.NormalizeNodeModel(profile.RuntimeModelId);
                string forcedRuntimeModel = (_main.IsAutoModelSelectionEnabled() &&
                    AiAutoCostPolicy.TryEnforceUserBlock(normalizedRuntime, out var blockedModel))
                    ? blockedModel
                    : normalizedRuntime;

                decision.RequestedAgentId = forcedAgent.Id;
                decision.ActualAgentId = forcedAgent.Id;

                decision.RequestedModelId = AiModelHelper.NormalizeNodeModel(profile.RuntimeModelId);
                decision.ModelId = forcedRuntimeModel;
                decision.ActualModelId = "";
                decision.ForceSingleModel = true;
                decision.ResolverLabel += " + Forced Agent Profile";
                decision.ResolverReason =
                    $"Delegated agent forced profile: {forcedAgent.Id} / model: {profile.RuntimeModelId}" +
                    (string.Equals(profile.RuntimeModelId, forcedRuntimeModel, StringComparison.OrdinalIgnoreCase)
                        ? ""
                        : $" / Auto cost policy: {profile.RuntimeModelId} → {forcedRuntimeModel}");
            }

            var runtimeAgent = AgentRegistry.Get(decision.ActualAgentId);
            bool allowAgentFirstAutomation =
                _main.IsAutoModelSelectionEnabled() &&
                _main.IsAdvancedAutoResolverEnabled();

            ApplyAutoCostPolicyToDecision(decision);

            _main.SetLiveDecisionResolving(node, decision);
            // 2. capability layer
            string capabilityAugmentedText = topText;

            var capabilityContext = new AgentExecutionContext
            {
                Node = node,
                Agent = runtimeAgent,
                TopText = topText,
                TaskMode = decision.TaskMode,
                Attachments = _main.GetAttachmentsForNode(node),
                AttachmentsRootDir = _main.GetAttachmentsRootDir()
            };

            var capabilityPlan = AgentCapabilityPlanner.Build(
    runtimeAgent,
    topText,
    decision.TaskMode,
    capabilityContext.Attachments != null && capabilityContext.Attachments.Count > 0);

            var orchestrationPlan = OrchestrationPlanner.Build(
                topText,
                decision,
                runtimeAgent,
                capabilityPlan,
                _main.IsAutoModelSelectionEnabled(),
                capabilityContext.Attachments != null && capabilityContext.Attachments.Count > 0);

            // Sub-agents (DelegationDepth > 0) write internal orchestration_plans only;
            // the root agent's plan is the authoritative visible one.
            bool isRootRun = request.DelegationDepth == 0;
            var orchestrationItem = AgentWorkspaceBuilder.FromCapabilityData(
                workspace,
                node,
                runtimeAgent.Id,
                "orchestration_plan",
                orchestrationPlan,
                isUserVisibleOverride: isRootRun ? (bool?)null : false);
            workspace.Add(orchestrationItem);

            // Orchestrator v1：執行狀態機。規劃階段在 Build 當下已完成，直接記為 success。
            var orchestration = new OrchestrationStateMachine(orchestrationPlan);
            orchestration.MarkSuccess("detect_task", $"TaskType={orchestrationPlan.TaskType}");
            orchestration.MarkSuccess("select_pipeline", orchestrationPlan.PipelineId);
            orchestration.MarkSuccess("select_agent", runtimeAgent.Id);
            orchestration.MarkSuccess("select_model", decision.ModelId);

            var workflowPlan = WorkflowPlanBuilder.FromOrchestrationPlan(
                orchestrationPlan,
                node.Id.ToString(),
                topText);

            workspace.Add(
                AgentWorkspaceBuilder.FromCapabilityData(
                    workspace,
                    node,
                    runtimeAgent.Id,
                    "workflow_plan",
                    workflowPlan));

            var downstreamNodePlan = DownstreamNodePlanBuilder.FromWorkflowPlan(workflowPlan);
            if (downstreamNodePlan.ProposedNodes.Count > 0)
            {
                workspace.Add(
                    AgentWorkspaceBuilder.FromCapabilityData(
                        workspace,
                        node,
                        runtimeAgent.Id,
                        "downstream_node_plan",
                        downstreamNodePlan));
            }

            System.Diagnostics.Debug.WriteLine(
                $"[CapabilityPlan] Agent={runtimeAgent.Id} Required={string.Join(", ", capabilityPlan.RequiredCapabilityIds)} Order={string.Join(" -> ", capabilityPlan.OrderedCapabilityIds)} Reason={capabilityPlan.Reason}");

            // Orchestrator v1：能力執行順序以 orchestration plan 為正式來源
            // （research_first 等 pipeline 的順序與必跑能力由它決定）。
            var orderedCapabilities = orchestrationPlan.CapabilityOrder
                .Select(id => AgentCapabilityRegistry.All.FirstOrDefault(c =>
                    string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
                .Where(c => c != null)
                .Cast<IAgentCapability>()
                .ToList();
            bool runCapabilityLayer =
                !request.SkipCapabilities;

            if (runCapabilityLayer)
            {
                foreach (var capability in orderedCapabilities)
                {
                    if (capability == null)
                        continue;

                    if (runtimeAgent.IsCapabilityBlocked(capability.Id))
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = false,
                            Executed = false,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "blocked by agent policy"
                        });
                        orchestration.MarkCapabilitySkipped(capability.Id, "blocked by agent policy");
                        continue;
                    }
                    bool isRequired = orchestrationPlan.IsCapabilityRequired(capability.Id);

                    if (!runtimeAgent.IsCapabilityAllowed(capability.Id) && !isRequired)
                    {
                        orchestration.MarkCapabilitySkipped(capability.Id, "agent policy not allowed");
                        continue;
                    }

                    // ? 關鍵：Required capability 強制允許
                    if (isRequired)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Capability Override] Agent={runtimeAgent.Id}, Capability={capability.Id} forced by planner");
                    }
                    if (capability.RequiredAgentCapability != AgentCapability.None &&
    !runtimeAgent.Capabilities.HasFlag(capability.RequiredAgentCapability) &&
    !isRequired)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = false,
                            Executed = false,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "agent capability not allowed"
                        });
                        orchestration.MarkCapabilitySkipped(capability.Id, "agent capability not allowed");
                        continue;
                    }

                    bool canHandle;
                    try
                    {
                        canHandle = capability.CanHandle(capabilityContext);
                    }
                    catch (Exception ex)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = false,
                            Executed = false,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = false,
                            Summary = "",
                            ErrorMessage = ex.Message
                        });
                        orchestration.MarkCapabilityFailed(capability.Id, $"CanHandle error: {ex.Message}");
                        continue;
                    }


                    if (!canHandle && !isRequired)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = false,
                            Executed = false,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "skipped"
                        });
                        orchestration.MarkCapabilitySkipped(capability.Id, "not applicable");
                        continue;
                    }

                    if (isRequired)
                        canHandle = true;

                    orchestration.MarkCapabilityRunning(capability.Id);

                    AgentCapabilityResult capabilityResult;
                    try
                    {
                        capabilityResult = await capability.ExecuteAsync(
                            capabilityContext,
                            request.CancellationToken);
                    }
                    catch (Exception ex)
                    {
                        System.Diagnostics.Debug.WriteLine(
                            $"[Capability Execute ERROR] Agent={runtimeAgent.Id}, Capability={capability.Id}, Error={ex}");

                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = false,
                            Summary = "",
                            ErrorMessage = ex.Message
                        });

                        orchestration.MarkCapabilityFailed(capability.Id, ex.Message);

                        if (orchestrationPlan.IsCapabilityRequired(capability.Id))
                        {
                            orchestration.CompleteRun(
                                executionSuccess: false,
                                failureDetail: $"Required capability failed: {capability.Id}");
                            orchestrationItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(orchestrationPlan);

                            throw new InvalidOperationException(
                                $"Required capability failed: {capability.Id}. {ex.Message}",
                                ex);
                        }

                        continue;
                    }
                    System.Diagnostics.Debug.WriteLine(
    $"[Capability Execute] " +
    $"Agent={runtimeAgent.Id}, " +
    $"Capability={capability.Id}, " +
    $"ResultNull={(capabilityResult == null)}, " +
    $"Handled={(capabilityResult == null ? false : capabilityResult.Handled)}, " +
    $"Keys={(capabilityResult?.Data == null ? "(null)" : string.Join(", ", capabilityResult.Data.Keys))}");


                    if (capabilityResult == null)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "null result"
                        });
                        orchestration.MarkCapabilitySuccess(capability.Id, "null result");
                        continue;
                    }

                    if (capabilityResult.Data != null && capabilityResult.Data.Count > 0)
                    {
                        foreach (var kv in capabilityResult.Data)
                        {
                            capabilityData[kv.Key] = kv.Value;

                            workspace.Add(
                                AgentWorkspaceBuilder.FromCapabilityData(
                                    workspace,
                                    node,
                                    runtimeAgent.Id,
                                    kv.Key,
                                    kv.Value));
                        }
                        System.Diagnostics.Debug.WriteLine(
                            $"[Capability] Agent={runtimeAgent.Id} Keys={string.Join(", ", capabilityData.Keys)}");

                        System.Diagnostics.Debug.WriteLine(
                            $"[Workspace] Count={workspace.GetAll().Count}");

                        System.Diagnostics.Debug.WriteLine(
                            $"[Workspace] Types={string.Join(", ", workspace.GetAll().Select(x => x.ItemType))}");
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = $"data produced: {string.Join(", ", capabilityResult.Data.Keys)}"
                        });
                        orchestration.MarkCapabilitySuccess(
                            capability.Id,
                            $"data: {string.Join(", ", capabilityResult.Data.Keys)}");
                    }

                    if (capabilityResult.Handled &&
                        !string.IsNullOrWhiteSpace(capabilityResult.Output) &&
                        (capabilityResult.Data == null || capabilityResult.Data.Count == 0))
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = true,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "direct handled"
                        });

                        var directExecution = new AiFallbackExecutionResult
                        {
                            IsSuccess = true,
                            Text = capabilityResult.Output,
                            ActualModelId = decision.ModelId,
                            UsedFallback = false,
                            Summary = $"handled by capability: {capability.Id}",
                            ErrorMessage = "",
                            Attempts = Array.Empty<AiFallbackAttempt>()
                        };

                        decision = _executionFinalizer.FinalizeDecision(decision, directExecution);
                        decision.ActualAgentId = runtimeAgent.Id;
                        decision.CapabilityTrace = capabilityTrace;
                        decision.DelegationTrace = delegationTrace;

                        orchestration.MarkCapabilitySuccess(capability.Id, "direct handled");
                        orchestration.MarkSuccess("write_workspace", $"artifacts: {workspace.GetAll().Count}");
                        orchestration.MarkSkipped("final_synthesis", "handled by capability");
                        orchestration.CompleteRun(executionSuccess: true);
                        orchestrationItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(orchestrationPlan);

                        return new AgentExecutionResult
                        {
                            Decision = decision,
                            Execution = directExecution,
                            CapabilityTrace = capabilityTrace,
                            DelegationTrace = delegationTrace
                        };
                    }

                    if (!string.IsNullOrWhiteSpace(capabilityResult.AugmentedPrompt))
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = true,
                            Success = true,
                            Summary = "prompt augmented"
                        });

                        orchestration.MarkCapabilitySuccess(capability.Id, "prompt augmented");

                        capabilityAugmentedText = capabilityResult.AugmentedPrompt;

                        capabilityContext = new AgentExecutionContext
                        {
                            Node = node,
                            Agent = runtimeAgent,
                            TopText = capabilityAugmentedText,
                            TaskMode = decision.TaskMode,
                            Attachments = _main.GetAttachmentsForNode(node),
                            AttachmentsRootDir = _main.GetAttachmentsRootDir()
                        };

                        continue;
                    }

                    if (capabilityResult.Data == null || capabilityResult.Data.Count == 0)
                    {
                        capabilityTrace.Add(new AgentCapabilityTraceItem
                        {
                            CapabilityId = capability.Id,
                            AgentId = runtimeAgent.Id,
                            CanHandle = true,
                            Executed = true,
                            Handled = false,
                            AugmentedPrompt = false,
                            Success = true,
                            Summary = "executed without output change"
                        });
                        orchestration.MarkCapabilitySuccess(capability.Id, "executed, no output change");
                    }
                }
            }
            if (runCapabilityLayer &&
                orchestrationPlan.RequiresFreshFacts &&
    !capabilityData.ContainsKey("verified_facts") &&
    !capabilityData.ContainsKey("search_summary"))
            {
                orchestration.CompleteRun(
                    executionSuccess: false,
                    failureDetail: "requires fresh facts but none produced");
                orchestrationItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(orchestrationPlan);

                throw new InvalidOperationException(
                    "This task requires fresh facts, but search-capability did not produce verified_facts or search_summary.");
            }

            // 2.5 parallel multi-agent execution
            bool parallelExecuted = false;
            AgentParallelExecutionResult? parallelResult = null;

            if (allowAgentFirstAutomation &&
                request.DelegationDepth == 0)
            {
                var parallelTasks = _parallelPlanner.Plan(
                    runtimeAgent,
                    capabilityAugmentedText,
                    decision.TaskMode);

                if (parallelTasks.Count > 0)
                {
                    parallelResult = await parallelRunner.RunAsync(
                        new AgentParallelExecutionRequest
                        {
                            Node = node,
                            OriginalInput = capabilityAugmentedText,
                            Tasks = parallelTasks,
                            Workspace = workspace
                        },
                        request.CancellationToken);

                    parallelExecuted = parallelResult.HasAnySuccess;

                    foreach (var r in parallelResult.Results)
                    {
                        workspace.Add(
                                AgentWorkspaceBuilder.FromCapabilityData(
                                    workspace,
                                    node,
                                    r.AgentId,
                                    "parallel_agent_output",
                                    new DelegateOutputPayload
                                    {
                                        FromAgentId = runtimeAgent.Id,
                                        ToAgentId = r.AgentId,
                                        Instruction = "parallel execution",
                                        Output = r.Output,
                                        Success = r.Success,
                                        ActualModelId = r.ModelId,
                                        ErrorMessage = r.ErrorMessage
                                    }));
                    }
                }
            }
            // 3. delegation
            IReadOnlyList<AgentDelegationRequest> plans;

            if (!allowAgentFirstAutomation || request.DelegationDepth >= 2)
            {
                plans = Array.Empty<AgentDelegationRequest>();
            }
            else
            {
                plans = _delegationPlanner.Plan(
                    runtimeAgent,
                    capabilityAugmentedText,
                    decision.TaskMode);
            }

            string delegatedContext = "";

            foreach (var plan in plans)
            {
                if (string.IsNullOrWhiteSpace(plan.TargetAgentId))
                    continue;

                var subAgent = AgentRegistry.Get(plan.TargetAgentId);

                try
                {
                    var subResult = await ExecuteAsync(new AgentExecutionRequest
                    {
                        Node = node,
                        Agent = subAgent,
                        TopText = plan.Instruction,
                        UseStreaming = false,
                        OnDelta = null,
                        DelegationDepth = request.DelegationDepth + 1,
                        ForceAgentProfile = true,
                        SkipCapabilities = true,
                        Workspace = workspace,
                        CancellationToken = request.CancellationToken
                    });

                    string subText = subResult.FinalText ?? "";

                    if (!string.IsNullOrWhiteSpace(subText))
                    {
                        delegatedContext +=
                            $"\n[Delegate:{subAgent.Id}]\n{subText}\n";
                    }

                    delegationTrace.Add(new AgentDelegationTraceItem
                    {
                        Depth = request.DelegationDepth + 1,
                        FromAgentId = runtimeAgent.Id,
                        ToAgentId = subAgent.Id,
                        Instruction = plan.Instruction,
                        OutputSummary = subText,
                        Success = subResult.IsSuccess,
                        ErrorMessage = ""
                    });

                    workspace.Add(
    AgentWorkspaceBuilder.FromCapabilityData(
        workspace,
        node,
        runtimeAgent.Id,
        "delegate_output",
       new DelegateOutputPayload
       {
           FromAgentId = runtimeAgent.Id,
           ToAgentId = subAgent.Id,
           Instruction = plan.Instruction,
           Output = subText,
           Success = subResult.IsSuccess,
           ActualModelId =
        !string.IsNullOrWhiteSpace(subResult.Decision?.ActualModelId)
            ? subResult.Decision.ActualModelId
            : subResult.Execution?.ActualModelId ?? "",
           ErrorMessage = ""
       }));

                    if (subResult.DelegationTrace != null && subResult.DelegationTrace.Count > 0)
                        delegationTrace.AddRange(subResult.DelegationTrace);
                }
                catch (Exception ex)
                {
                    delegationTrace.Add(new AgentDelegationTraceItem
                    {
                        Depth = request.DelegationDepth + 1,
                        FromAgentId = runtimeAgent.Id,
                        ToAgentId = subAgent.Id,
                        Instruction = plan.Instruction,
                        OutputSummary = "",
                        Success = false,
                        ErrorMessage = ex.Message
                    });

                    workspace.Add(
    AgentWorkspaceBuilder.FromCapabilityData(
        workspace,
        node,
        runtimeAgent.Id,
        "delegate_output",
        new DelegateOutputPayload
        {
            FromAgentId = runtimeAgent.Id,
            ToAgentId = subAgent.Id,
            Instruction = plan.Instruction,
            Output = "",
            Success = false,

            // ?? 加這行
            ActualModelId = "",

            ErrorMessage = ex.Message
        }));
                }
            }
            AiFallbackExecutionResult? synthesisExecution = null;

            if (parallelExecuted && request.DelegationDepth == 0)
            {
                synthesisExecution = await RunFinalSynthesisAsync(
                    node,
                    runtimeAgent,
                    capabilityAugmentedText,
                    workspace,
                    decision,
                    request.PreferenceBlock,
                    request.CancellationToken);

                if (synthesisExecution != null)
                {
                    synthesisExecution = FinalAnswerSanitizer.Sanitize(
                        synthesisExecution,
                        enforceSynthesisFormat: workspace.GetByType("verified_facts").Count > 0);
                }

                if (synthesisExecution != null && synthesisExecution.IsSuccess)
                {
                    workspace.Add(
                        AgentWorkspaceBuilder.FromCapabilityData(
                            workspace,
                            node,
                            "general-agent",
                            "final_synthesis",
                            new FinalSynthesisPayload
                            {
                                SynthesizerAgentId = "general-agent",
                                ModelId = synthesisExecution.ActualModelId ?? "",
                                Output = synthesisExecution.Text ?? "",
                                Success = true,
                                ErrorMessage = ""
                            }));
                }
            }
            // 4. merge
            string capabilityDataBlock = BuildCapabilityDataBlock(capabilityData);
            string workspaceBlock = workspace.BuildPromptBlock();
            bool hasVerifiedFacts =
                capabilityData.ContainsKey("verified_facts") ||
                workspace.GetByType("verified_facts").Count > 0;
            bool hasCodeDiffDraft =
                capabilityData.ContainsKey("code_diff_draft") ||
                workspace.GetByType("code_diff_draft").Count > 0;

            // Code Agent v1.5：bug 清單模式偵測 + 大型任務規模/風險評估（過程資訊，放決策區）。
            string codeRequestType = TryGetCodeRequestType(capabilityData, workspace);
            bool isBugListing = string.Equals(codeRequestType, "bug_listing", StringComparison.OrdinalIgnoreCase);

            var codeSnapshotForAssessment = TryGetCodeSnapshot(capabilityData, workspace);
            CodeTaskAssessmentPayload? codeAssessment = null;
            if (codeSnapshotForAssessment != null)
            {
                codeAssessment = CodeTaskAssessor.Assess(codeSnapshotForAssessment);
                workspace.Add(
                    AgentWorkspaceBuilder.FromCapabilityData(
                        workspace,
                        node,
                        runtimeAgent.Id,
                        "code_task_assessment",
                        codeAssessment));
            }

            // 三段 finalInput 組裝都用同一套 code 指令區塊，避免漏掉某條路徑。
            void AddCodeInstructionBlocks(List<string> parts)
            {
                if (isBugListing)
                    parts.Add(BuildBugListingInstructionBlock());

                if (hasCodeDiffDraft)
                    parts.Add(BuildCodeDiffOutputInstructionBlock());

                if (codeAssessment != null &&
                    !string.Equals(codeAssessment.RiskLevel, "low", StringComparison.OrdinalIgnoreCase))
                {
                    parts.Add(BuildCodeRiskNoteBlock(codeAssessment));
                }
            }

            bool isFinanceTask = FinanceTaskDetector.IsFinanceFocused(capabilityAugmentedText);
            bool enforceFinalSynthesisFormat = hasVerifiedFacts && isFinanceTask;

            string finalInput = capabilityAugmentedText;

            if (!string.IsNullOrWhiteSpace(capabilityDataBlock) ||
    !string.IsNullOrWhiteSpace(workspaceBlock))
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(workspaceBlock))
                    parts.Add(workspaceBlock);

                if (!string.IsNullOrWhiteSpace(capabilityDataBlock))
                    parts.Add(capabilityDataBlock);

                if (enforceFinalSynthesisFormat)
                    parts.Add(BuildFinalOutputFormatBlock());

                AddCodeInstructionBlocks(parts);

                parts.Add("【目前任務】\n" + capabilityAugmentedText);

                finalInput = string.Join("\n\n", parts);
            }

            if (!string.IsNullOrWhiteSpace(delegatedContext) && !hasVerifiedFacts)
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(workspaceBlock))
                    parts.Add(workspaceBlock);

                if (!string.IsNullOrWhiteSpace(capabilityDataBlock))
                    parts.Add(capabilityDataBlock);

                if (enforceFinalSynthesisFormat)
                    parts.Add(BuildFinalOutputFormatBlock());

                AddCodeInstructionBlocks(parts);

                parts.Add(
                    "以下是其他代理提供的補充資訊：\n" +
                    delegatedContext);

                parts.Add("請基於以上資訊完成目前任務：\n" + capabilityAugmentedText);

                finalInput =
                    string.Join("\n\n", parts);
            }
            else if (!string.IsNullOrWhiteSpace(delegatedContext) && hasVerifiedFacts)
            {
                var parts = new List<string>();

                if (!string.IsNullOrWhiteSpace(workspaceBlock))
                    parts.Add(workspaceBlock);

                if (!string.IsNullOrWhiteSpace(capabilityDataBlock))
                    parts.Add(capabilityDataBlock);

                if (enforceFinalSynthesisFormat)
                    parts.Add(BuildFinalOutputFormatBlock());
                AddCodeInstructionBlocks(parts);
                parts.Add("【Delegated Analysis Omitted】\nDelegated/parallel agent text was omitted because structured verified_facts exist. Use delegated analysis only as internal reasoning, not as a source for numeric facts.");
                parts.Add("【目前任務】\n" + capabilityAugmentedText);

                finalInput = string.Join("\n\n", parts);
            }

            // Image Gen v1：圖片任務不需要 LLM 描述圖片，只讓它輸出一句確認語。
            if (orchestrationPlan.TaskType == OrchestrationTaskType.ImageGeneration)
            {
                finalInput =
                    "你是圖片生成助理。使用者請你依描述生成圖片，圖片生成程式已準備好。" +
                    "請只用一句繁體中文確認你會根據描述生成圖片，不要展開描述圖片內容，不要給提示詞。" +
                    "\n\n【使用者描述】\n" + topText;
            }
            else if (!string.IsNullOrWhiteSpace(request.PreferenceBlock))
            {
                // 非 parallel 路徑：把使用者偏好放在最前面當最高優先指令（圖片任務除外）。
                finalInput =
                    request.PreferenceBlock.Trim() +
                    "\n（以上偏好為最高優先，蓋過任何「使用繁體中文」等預設規則；若指定輸出語言或格式，必須改用該語言與格式。）\n\n" +
                    finalInput;
            }

            // 5. execution
            orchestration.MarkRunning("final_synthesis");

            AiFallbackExecutionResult execution;
            // 圖片任務不串流：確認語很短，且串流後還要 append「已生成圖片」/錯誤訊息，
            // 串流會讓最終修改與清理（含 [[END_OF_RESPONSE]] 移除）來不及蓋回畫面。
            bool useStreamingForFinalExecution =
                request.UseStreaming &&
                !hasCodeDiffDraft &&
                orchestrationPlan.TaskType != OrchestrationTaskType.ImageGeneration;

            if (synthesisExecution != null && synthesisExecution.IsSuccess)
            {
                execution = FinalAnswerSanitizer.Sanitize(
                    synthesisExecution,
                    enforceSynthesisFormat: enforceFinalSynthesisFormat);

                if (request.UseStreaming && request.OnDelta != null &&
                    !string.IsNullOrWhiteSpace(execution.Text))
                {
                    request.OnDelta(execution.Text);
                }
            }
            else
            {
                execution = await _executeWithFallbackAsync(
                    node,
                    finalInput,
                    decision,
                    useStreamingForFinalExecution ? request.OnDelta : null,
                    useStreamingForFinalExecution,
                    request.CancellationToken);

                execution = FinalAnswerSanitizer.Sanitize(
                    execution,
                    enforceSynthesisFormat: enforceFinalSynthesisFormat);
            }

            if (hasCodeDiffDraft)
            {
                CodeDiffArtifactPayload? readyDiffForValidation = null;
                CodeDiffValidationPayload? validationForReadyDiff = null;

                var readyDiff = CodeDiffArtifactExtractor.TryExtractReadyDiff(
                    execution.Text,
                    capabilityAugmentedText);

                if (readyDiff != null)
                {
                    workspace.Add(
                        AgentWorkspaceBuilder.FromCapabilityData(
                            workspace,
                            node,
                            runtimeAgent.Id,
                            "code_diff",
                            readyDiff));

                    var snapshot = capabilityData.TryGetValue("code_file_snapshot", out var snapshotValue)
                        ? snapshotValue as CodeFileSnapshotPayload
                        : workspace.GetByType("code_file_snapshot")
                            .Select(x => x.Payload as CodeFileSnapshotPayload)
                            .FirstOrDefault(x => x != null);

                    var validation = CodeDiffDryRunValidator.Validate(
                        readyDiff,
                        snapshot);

                    workspace.Add(
                        AgentWorkspaceBuilder.FromCapabilityData(
                            workspace,
                            node,
                            runtimeAgent.Id,
                            "code_diff_validation",
                            validation));

                    readyDiffForValidation = readyDiff;
                    validationForReadyDiff = validation;
                }

                if (readyDiffForValidation != null &&
                    validationForReadyDiff != null &&
                    string.Equals(validationForReadyDiff.Status, "invalid", StringComparison.OrdinalIgnoreCase))
                {
                    var repairedExecution = await TryRepairInvalidCodeDiffAsync(
                        node,
                        finalInput,
                        capabilityAugmentedText,
                        execution,
                        readyDiffForValidation,
                        validationForReadyDiff,
                        decision,
                        request.CancellationToken);

                    if (repairedExecution != null)
                    {
                        var repairedDiff = CodeDiffArtifactExtractor.TryExtractReadyDiff(
                            repairedExecution.Text,
                            capabilityAugmentedText);

                        if (repairedDiff != null)
                        {
                            workspace.Add(
                                AgentWorkspaceBuilder.FromCapabilityData(
                                    workspace,
                                    node,
                                    runtimeAgent.Id,
                                    "code_diff",
                                    repairedDiff));

                            var snapshot = capabilityData.TryGetValue("code_file_snapshot", out var snapshotValue)
                                ? snapshotValue as CodeFileSnapshotPayload
                                : workspace.GetByType("code_file_snapshot")
                                    .Select(x => x.Payload as CodeFileSnapshotPayload)
                                    .FirstOrDefault(x => x != null);

                            var repairedValidation = CodeDiffDryRunValidator.Validate(
                                repairedDiff,
                                snapshot);

                            workspace.Add(
                                AgentWorkspaceBuilder.FromCapabilityData(
                                    workspace,
                                    node,
                                    runtimeAgent.Id,
                                    "code_diff_validation",
                                    repairedValidation));

                            execution = repairedExecution;

                            if (string.Equals(repairedValidation.Status, "invalid", StringComparison.OrdinalIgnoreCase))
                            {
                                execution = WithSafeInvalidPatchMessage(
                                    execution,
                                    repairedValidation);
                            }
                        }
                    }
                }
                else if (validationForReadyDiff != null &&
                    string.Equals(validationForReadyDiff.Status, "invalid", StringComparison.OrdinalIgnoreCase))
                {
                    execution = WithSafeInvalidPatchMessage(
                        execution,
                        validationForReadyDiff);
                }

                if (request.UseStreaming &&
                    request.OnDelta != null &&
                    !useStreamingForFinalExecution &&
                    !string.IsNullOrWhiteSpace(execution.Text))
                {
                    request.OnDelta(execution.Text);
                }
            }

            // 6. finalize
            if (execution.IsSuccess)
                orchestration.MarkSuccess("final_synthesis", $"model: {execution.ActualModelId}");
            else
                orchestration.MarkFailed("final_synthesis", execution.ErrorMessage);

            // §6 第一層輸出判斷：優先用 NodeService 先跑 API 得到的 OutputIntent（要簡報/報告/表格哪幾個）；
            // 沒有時（例如子代理委派）退回關鍵字 + TaskType。報告→docx、表格→xlsx、簡報→pptx，且一律再配一份 pdf。
            OutputIntent intent = request.OutputIntent ?? new OutputIntent
            {
                WantsReport = orchestrationPlan.TaskType == OrchestrationTaskType.GenerateFile
                              || OutputFormatDetector.WantsWrittenReport(capabilityAugmentedText),
                WantsTable = OutputFormatDetector.WantsSpreadsheet(capabilityAugmentedText),
                WantsPresentation = orchestrationPlan.TaskType == OrchestrationTaskType.Presentation
                                    || OutputFormatDetector.WantsPresentation(capabilityAugmentedText)
            };

            bool doReport = intent.WantsReport;
            bool doTable = intent.WantsTable;
            bool doDeck = intent.WantsPresentation;

            // 報告 / 表格：GenerateReportFile 內部依 intent 決定 .docx（報告）/ .xlsx（表格），且一律配一份 .pdf。
            if (execution.IsSuccess &&
                (doReport || doTable) &&
                request.DelegationDepth == 0 &&
                !string.IsNullOrWhiteSpace(execution.Text))
            {
                execution = await GenerateReportFile(
                    node,
                    runtimeAgent,
                    capabilityAugmentedText,
                    workspace,
                    orchestrationPlan,
                    execution,
                    orchestration,
                    intent,
                    request.CancellationToken);
            }

            // 簡報：輸出投影片大綱 + .pptx。若同一請求也產了報告/表格（已配 pdf）就用 append 模式接文字、不再配 pdf；
            // 若這次只有簡報，alsoPdf=true 由簡報這邊補一份 deck 的 .pdf（不管輸出什麼都要配一個 pdf）。
            if (execution.IsSuccess &&
                doDeck &&
                request.DelegationDepth == 0 &&
                !string.IsNullOrWhiteSpace(execution.Text))
            {
                execution = await GeneratePresentation(
                    node,
                    runtimeAgent,
                    capabilityAugmentedText,
                    workspace,
                    orchestrationPlan,
                    execution,
                    orchestration,
                    request.CancellationToken,
                    appendToText: doReport || doTable);
            }

            // Image Gen v1：ImageGeneration 任務在最終答案後呼叫 DALL-E 3 生成圖片，
            // 存檔、加入 workspace artifact，並在輸出區直接顯示。
            if (execution.IsSuccess &&
                orchestrationPlan.TaskType == OrchestrationTaskType.ImageGeneration &&
                request.DelegationDepth == 0)
            {
                execution = await GenerateImageFile(
                    node,
                    runtimeAgent,
                    topText,
                    workspace,
                    orchestrationPlan,
                    execution,
                    orchestration,
                    request.CancellationToken);
            }

            // Video Gen v1：VideoGeneration 任務在最終答案後呼叫影片 API（非同步、需輪詢），
            // 完成後存檔、加入 workspace artifact。未啟用時（休眠）標記未啟用，不影響主答案。
            if (execution.IsSuccess &&
                orchestrationPlan.TaskType == OrchestrationTaskType.VideoGeneration &&
                request.DelegationDepth == 0)
            {
                execution = await GenerateVideoFile(
                    node,
                    runtimeAgent,
                    topText,
                    workspace,
                    orchestrationPlan,
                    execution,
                    orchestration,
                    request.CancellationToken);
            }

            orchestration.MarkSuccess("write_workspace", $"artifacts: {workspace.GetAll().Count}");
            orchestration.CompleteRun(execution.IsSuccess, execution.ErrorMessage);
            orchestrationItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(orchestrationPlan);

            var workspaceSummary = workspace.BuildSummary();
            decision = _executionFinalizer.FinalizeDecision(decision, execution);
            decision.ActualAgentId = runtimeAgent.Id;
            decision.CapabilityTrace = capabilityTrace;
            decision.DelegationTrace = delegationTrace;
            decision.WorkspaceSummary = workspaceSummary?.SummaryText ?? "";
            decision.WorkspaceArtifactDetails = workspaceSummary?.ArtifactDetails ?? Array.Empty<string>();
            decision.WorkspaceArtifacts = workspaceSummary?.Artifacts ?? Array.Empty<AgentWorkspaceArtifactRecord>();


            return new AgentExecutionResult
            {
                Decision = decision,
                Execution = execution,
                CapabilityTrace = capabilityTrace,
                DelegationTrace = delegationTrace,
                WorkspaceSummary = workspaceSummary
            };
        }
        /// <summary>
        /// File Generation v1：把最終答案寫成 Markdown 報告檔，加入 workspace artifact，
        /// 並在答案尾端附上檔案位置說明。回傳（可能被附註過的）execution。
        /// 寫檔失敗不影響主答案，只把 generate_file 階段標記為 failed。
        /// </summary>
        private async Task<AiFallbackExecutionResult> GenerateReportFile(
            NodeControl node,
            AgentDefinition runtimeAgent,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            OutputIntent intent,
            CancellationToken ct)
        {
            orchestration.MarkRunning("generate_file");

            int factCount = workspace.GetByType("verified_facts")
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .Sum(p => p.Facts?.Count ?? 0);

            string sourceSummary = factCount > 0
                ? $"{orchestrationPlan.PipelineId} / {factCount} 筆 verified_facts"
                : orchestrationPlan.PipelineId;

            // ── 內容作者：報告代理 + 表格代理「同時」整理乾淨內容（多個 agent 並行工作）。──
            // 不再直接把主答案（可能是 ASCII 簡報草稿）塞進檔案，避免「亂做」。
            // 主答案已是乾淨散文 / 已含表格時就直接用，不額外呼叫模型（省成本、不改壞）。
            string sourceMaterial = execution.Text ?? "";
            bool dirty = DocumentAuthor.LooksDirty(sourceMaterial);
            string? existingTable = DocumentAuthor.ExtractMarkdownTable(sourceMaterial);

            bool authorReport = intent.WantsReport && dirty;
            bool authorTable = intent.WantsTable && existingTable == null;

            if (authorReport || authorTable)
                node.SetLoadingHint(authorReport && authorTable
                    ? "報告與表格代理同時整理內容中"
                    : authorTable ? "表格代理整理內容中" : "報告代理整理內容中");

            Task<string?> reportTask = authorReport
                ? AuthorCleanAsync(node, DocumentAuthor.BuildReportPrompt(userInput, sourceMaterial), "Report Author", ct)
                : Task.FromResult<string?>(null);

            Task<string?> tableTask = authorTable
                ? AuthorCleanAsync(node, DocumentAuthor.BuildTablePrompt(userInput, sourceMaterial), "Table Author", ct)
                : Task.FromResult<string?>(null);

            await Task.WhenAll(reportTask, tableTask).ConfigureAwait(false);
            node.SetLoadingHint(null);

            // 報告正文：作者成功用作者內容；否則退回「清理過」的主答案（去 ASCII 線框）。
            string reportBody = !string.IsNullOrWhiteSpace(reportTask.Result)
                ? reportTask.Result!
                : DocumentAuthor.Sanitize(sourceMaterial);

            // 表格 Markdown：作者成功用作者表格；否則用主答案裡既有的表格；再退回清理。
            string tableMarkdown = !string.IsNullOrWhiteSpace(tableTask.Result)
                ? tableTask.Result!
                : (existingTable ?? DocumentAuthor.Sanitize(sourceMaterial));

            // 報告 Markdown（含中繼資料 + 來源）：docx 與 report.pdf 都用同一份 → 內容完全一致。
            string reportMarkdown = MarkdownReportBuilder.Build(new MarkdownReportBuilder.Request
            {
                UserInput = userInput,
                FinalAnswer = reportBody,
                Workspace = workspace,
                TaskType = orchestrationPlan.TaskType,
                PipelineId = orchestrationPlan.PipelineId,
                ModelId = execution.ActualModelId ?? orchestrationPlan.ModelId,
                AgentId = runtimeAgent?.Id ?? ""
            });

            string title = ExtractReportTitle(userInput);
            string genDir = _main.GetGeneratedFilesDir();
            var producedFiles = new List<string>();
            string lastError = "";

            // 各檔以各自的「代理身分」入庫，工作區才看得到報告代理 / 表格代理分工。
            void TryEmit(Func<GeneratedFilePayload> writer, string agentId)
            {
                try
                {
                    var result = writer();
                    if (result != null && result.Success)
                    {
                        workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                            workspace, node, agentId, "generated_file", result));
                        producedFiles.Add(result.FileName);
                    }
                    else if (result != null && !string.IsNullOrWhiteSpace(result.ErrorMessage))
                    {
                        lastError = result.ErrorMessage;
                    }
                }
                catch (Exception ex)
                {
                    lastError = ex.Message;
                }
            }

            // 報告：.docx + 內容一致的 report.pdf（兩者都吃 reportMarkdown）。
            if (intent.WantsReport)
            {
                TryEmit(() => GeneratedFileWriter.WriteDocx(
                    genDir, title, DocxReportBuilder.Build(reportMarkdown), sourceSummary), "report-agent");
                TryEmit(() => GeneratedFileWriter.WritePdf(
                    genDir, title, PdfReportBuilder.Build(reportMarkdown), sourceSummary), "report-agent");
            }

            // 表格：.xlsx + 內容一致的 table.pdf（兩者都吃 tableMarkdown）。
            if (intent.WantsTable)
            {
                string tablePdfMarkdown = $"# {title}\n\n{tableMarkdown}\n";
                TryEmit(() => GeneratedFileWriter.WriteXlsx(
                    genDir, title, XlsxReportBuilder.Build(tableMarkdown, title), sourceSummary), "table-agent");
                // 報告也在時，表格 pdf 用不同檔名避免和報告 pdf 撞名。
                string tablePdfTitle = intent.WantsReport ? title + "（表格）" : title;
                TryEmit(() => GeneratedFileWriter.WritePdf(
                    genDir, tablePdfTitle, PdfReportBuilder.Build(tablePdfMarkdown), sourceSummary), "table-agent");
            }

            if (producedFiles.Count > 0)
                orchestration.MarkSuccess("generate_file", string.Join(" / ", producedFiles));
            else
                orchestration.MarkFailed("generate_file", string.IsNullOrWhiteSpace(lastError) ? "報告寫檔失敗" : lastError);

            // 報告文字顯示在對話框，各檔 chip 提供下載，兩者並存。
            return execution;
        }

        private static string ExtractReportTitle(string userInput)
        {
            string text = (userInput ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return "報告";

            return text.Length > 40 ? text.Substring(0, 40).Trim() : text;
        }

        /// <summary>
        /// Presentation Agent v1：把最終答案拆解成投影片大綱，加入 PresentationOutline artifact，
        /// 渲染成 Marp Markdown deck 並寫檔（同時產生 GeneratedFile artifact），最後在答案尾端附上說明。
        /// 寫檔失敗不影響主答案，只把 presentation_outline 階段標記為 failed。
        /// </summary>
        private async Task<AiFallbackExecutionResult> GeneratePresentation(
            NodeControl node,
            AgentDefinition runtimeAgent,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            CancellationToken ct,
            bool appendToText = false)
        {
            orchestration.MarkRunning("presentation_outline");

            int factCount = workspace.GetByType("verified_facts")
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .Sum(p => p.Facts?.Count ?? 0);

            string sourceSummary = factCount > 0
                ? $"{orchestrationPlan.PipelineId} / {factCount} 筆 verified_facts"
                : orchestrationPlan.PipelineId;

            // Presentation v1.5（Gamma 之前）：先用 Perplexity 查資料，再請 Claude/GPT 真正「設計」一份簡報。
            // 兩段任一失敗都優雅退回原本的確定性切段（PresentationOutlineBuilder），demo 不會開天窗。
            int requestedSlides = PresentationOutlineBuilder.DetectRequestedSlideCount(userInput);
            var outline = await BuildAuthoredPresentationAsync(
                              node, userInput, workspace, execution, requestedSlides, orchestrationPlan, runtimeAgent, ct)
                          ?? PresentationOutlineBuilder.Build(new PresentationOutlineBuilder.Request
                          {
                              UserInput = userInput,
                              FinalAnswer = execution.Text ?? "",
                              Workspace = workspace,
                              PipelineId = orchestrationPlan.PipelineId,
                              ModelId = execution.ActualModelId ?? orchestrationPlan.ModelId,
                              AgentId = runtimeAgent?.Id ?? ""
                          });

            // §7.1 投影片張數精準控制：使用者指定張數時，把內容投影片數修正到剛好那麼多
            //（作者模型沒照辦、或確定性切段忽略張數時的安全網）。
            if (requestedSlides > 0)
                outline = PresentationOutlineBuilder.EnforceSlideCount(outline, requestedSlides);

            node.SetLoadingHint(null);

            // 結構化大綱 artifact（slide plan）。
            workspace.Add(
                AgentWorkspaceBuilder.FromCapabilityData(
                    workspace,
                    node,
                    runtimeAgent?.Id ?? "presentation-agent",
                    "presentation_outline",
                    outline));

            // Image Gen → Presentation：使用者明確要求配圖時，生成一張封面圖嵌入 deck / pptx（不另外輸出 png chip）。
            byte[]? coverImageBytes = null;
            if (PresentationWantsCoverImage(userInput))
            {
                (coverImageBytes, _) = await TryGenerateCoverImageAsync(
                    node, runtimeAgent, outline, workspace, orchestrationPlan, ct);
            }

            // .pptx 二進位檔：只有使用者在個人化選了「Gamma」生成器才走 Gamma（Claude 內容 + Gamma 設計）；
            // 其餘（Claude / GPT）一律用內建 PptxBuilder。Gamma 未設定金鑰或失敗時也會回 null → fallback。
            // 目前 Gamma 在 UI 停用且 SetPresentationEngine 會把 Gamma 落回 Claude，故此分支現階段不會觸發。
            string? gammaUrl = _main.GetPresentationEngine() == PresentationEngine.Gamma
                ? await TryAddGammaPptxAsync(
                    node, runtimeAgent, outline, execution.Text ?? "", workspace, sourceSummary, ct)
                : null;

            GeneratedFilePayload? pptxResult = null;
            if (gammaUrl == null)
            {
                try
                {
                    byte[] pptxBytes = PptxBuilder.Build(outline, coverImageBytes);
                    pptxResult = GeneratedFileWriter.WritePptx(
                        _main.GetGeneratedFilesDir(),
                        title: outline.Title,
                        content: pptxBytes,
                        sourceSummary: sourceSummary);

                    if (pptxResult.Success)
                        workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                            workspace, node, runtimeAgent?.Id ?? "presentation-agent",
                            "generated_file", pptxResult));
                }
                catch { }
            }

            // 簡報一律配一份「分頁 / 版面 / 封面圖都與 pptx 一致」的 deck.pdf（一張投影片一頁，同一份 outline + 同一張封面圖）。
            try
            {
                var pdfResult = GeneratedFileWriter.WritePdf(
                    _main.GetGeneratedFilesDir(),
                    title: outline.Title,
                    content: DeckPdfBuilder.Build(outline, coverImageBytes),
                    sourceSummary: sourceSummary);

                if (pdfResult.Success)
                    workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                        workspace, node, runtimeAgent?.Id ?? "presentation-agent",
                        "generated_file", pdfResult));
            }
            catch { }

            if (pptxResult?.Success == true || gammaUrl != null)
            {
                string fileName = pptxResult?.FileName ?? "Gamma 線上版";
                orchestration.MarkSuccess("presentation_outline", $"{outline.SlideCount} 張 / {fileName}");

                string note = $"已生成簡報大綱：{outline.SlideCount} 張投影片。";
                if (!string.IsNullOrWhiteSpace(gammaUrl))
                    note += $"\n線上版（Gamma）：{gammaUrl}";

                // 同一請求也產了書面報告時，把簡報說明接在報告文字之後，不覆蓋報告內容。
                string combinedText = appendToText && !string.IsNullOrWhiteSpace(execution.Text)
                    ? execution.Text.TrimEnd() + "\n\n" + note
                    : note;

                return new AiFallbackExecutionResult
                {
                    IsSuccess = execution.IsSuccess,
                    Text = combinedText,
                    ActualModelId = execution.ActualModelId ?? "",
                    UsedFallback = execution.UsedFallback,
                    Summary = execution.Summary ?? "",
                    ErrorMessage = execution.ErrorMessage ?? "",
                    Attempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>()
                };
            }

            orchestration.MarkFailed("presentation_outline", pptxResult?.ErrorMessage ?? "pptx 寫檔失敗");
            return execution;
        }

        // Presentation v2 前置：用 Gamma 產出高品質 .pptx（Claude 內容 + Gamma 設計）。
        // 回傳 gammaUrl（成功，可能為空字串表示無連結）；回傳 null 代表未啟用或失敗，呼叫端應 fallback 到 PptxBuilder。
        // 目前休眠：沒有 GAMMA_API_KEY 時 IsConfigured=false，直接回 null，行為與現在相同。
        private async Task<string?> TryAddGammaPptxAsync(
            NodeControl node,
            AgentDefinition? runtimeAgent,
            PresentationOutlinePayload outline,
            string contentText,
            AgentWorkspace workspace,
            string sourceSummary,
            CancellationToken ct)
        {
            var gamma = new GammaPresentationService();
            if (!gamma.IsConfigured || string.IsNullOrWhiteSpace(contentText))
                return null;

            try
            {
                var result = await gamma.GeneratePresentationAsync(new GammaGenerationInput
                {
                    InputText = contentText,
                    Title = outline.Title,
                    NumCards = outline.RequestedSlideCount,
                    Language = "zh-tw"
                }, ct);

                if (!result.Success || !result.HasExport)
                    return null;

                byte[] pptxBytes = await gamma.DownloadExportAsync(result.ExportUrl, ct);
                if (pptxBytes == null || pptxBytes.Length == 0)
                    return null;

                var pptxGenerated = GeneratedFileWriter.WritePptx(
                    _main.GetGeneratedFilesDir(),
                    title: outline.Title,
                    content: pptxBytes,
                    sourceSummary: $"{sourceSummary} / Gamma");

                if (!pptxGenerated.Success)
                    return null;

                workspace.Add(AgentWorkspaceBuilder.FromCapabilityData(
                    workspace, node, runtimeAgent?.Id ?? "presentation-agent",
                    "generated_file", pptxGenerated));

                return result.GammaUrl ?? "";
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 任何問題（網路 / 欄位不符 / 額度不足）→ 回 null 讓呼叫端 fallback 到 PptxBuilder。
                return null;
            }
        }

        // 使用者是否明確要求簡報配圖（避免每次簡報都呼叫圖片 API 造成額外成本）。
        private static bool PresentationWantsCoverImage(string? userInput)
        {
            string text = userInput ?? "";
            string lower = text.ToLowerInvariant();

            return ContainsAnyText(text, lower,
                "配圖", "插圖", "封面圖", "加圖", "加上圖", "附圖", "示意圖", "搭配圖",
                "有圖", "要圖", "圖片簡報", "帶圖", "封面圖片",
                "含圖", "含圖片", "包含圖", "需要圖", "加入圖", "放圖", "要有圖", "帶圖片",
                "with image", "cover image", "with picture", "illustration", "with a picture");
        }

        private static bool ContainsAnyText(string text, string lower, params string[] keywords)
        {
            foreach (var kw in keywords)
            {
                if (string.IsNullOrEmpty(kw))
                    continue;

                if (kw.Any(ch => ch > 127))
                {
                    if (text.Contains(kw, StringComparison.Ordinal))
                        return true;
                }
                else if (lower.Contains(kw, StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        // 為簡報生成一張封面圖：回傳 (png bytes, 寫出的檔名)；失敗時回傳 (null, null) 不影響簡報。
        private async Task<(byte[]?, string?)> TryGenerateCoverImageAsync(
            NodeControl node,
            AgentDefinition? runtimeAgent,
            PresentationOutlinePayload outline,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            CancellationToken ct)
        {
            try
            {
                string subject = string.IsNullOrWhiteSpace(outline.Topic)
                    ? outline.Title
                    : $"{outline.Title}：{outline.Topic}";

                string prompt =
                    $"專業簡報封面用的乾淨示意插圖，主題：{subject}。" +
                    "簡潔現代、留白充足、無文字、無浮水印。";

                var imageService = new OpenAIImageService("gpt-image-2");
                var image = await imageService.GenerateAsync(prompt, "1024x1024", ct);

                if (!image.Success || image.PngBytes == null || image.PngBytes.Length == 0)
                    return (null, null);

                // 封面圖只嵌進 .pptx 封面頁，不另外寫成 .png 檔、也不加進 workspace 產出物，
                // 避免在輸出區多出一個獨立的封面 png chip（使用者只要簡報，不要散落的封面圖）。
                return (image.PngBytes, null);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 配圖失敗不影響簡報主體。
                return (null, null);
            }
        }

        /// <summary>
        /// Image Gen v1：呼叫 DALL-E 3 依使用者描述生成圖片，存成 .png、加入 GeneratedFile artifact，
        /// 並在答案尾端附上一句說明（圖片本體由輸出區直接顯示，檔案 chip 可開啟原圖）。
        /// 生成失敗不影響主答案，只把 generate_image 階段標記為 failed。
        /// </summary>
        // 把（可能含上游搜尋結果的）原始輸入交給 Claude，萃取真正主體並寫成具體、可畫的圖片提示。
        private async Task<string> BuildImageBriefAsync(NodeControl node, string rawRequest, CancellationToken ct)
        {
            string briefPrompt =
                "你是圖片生成的提示詞工程師。下面是使用者的請求，可能夾帶上游節點的搜尋結果或一大段背景文字。\n" +
                "請閱讀全部內容，找出『真正要畫的主體』（例如某家真實公司、某個產品、某個場景），" +
                "再寫出一段【具體、精煉】的圖片生成提示，直接描述畫面：主體、場景、構圖、風格、色調。\n\n" +
                "嚴格規則：\n" +
                "1. 只輸出最終的圖片提示文字本身，不要任何解釋、前後綴、引號或 markdown。\n" +
                "2. 讓人一眼認得出主體。若主體是知名、可公開查證的真實品牌（例如 Google、Apple、星巴克），" +
                "請納入其真實、為人熟知的視覺特徵（招牌配色、總部 / 門市建築、識別意象），使畫面明確指向該品牌。\n" +
                "3. 但不要捏造不存在的東西：若主體是不知名或私人公司、且背景資料沒有提供其真實的商標 / 視覺識別，" +
                "就不要憑空發明 logo、品牌名、標語或數據，改以該產業的真實情境（廠房、產品、場域、員工工作畫面）呈現。\n" +
                "4. 風格預設為乾淨、現代、專業的示意插圖或攝影，留白充足、無浮水印。\n\n" +
                "=== 使用者請求與背景資料 ===\n" + rawRequest;

            var briefDecision = new NodeExecutionDecision
            {
                RequestedAgentId = "general-agent",
                ActualAgentId = "general-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(AiModels.Claude_Sonnet46),
                ModelId = AiModelHelper.NormalizeNodeModel(AiModels.Claude_Sonnet46),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = "Image Brief (Claude)",
                ResolverReason = "Claude 先把上游內容轉成具體的圖片提示，避免圖片模型亂編商標 / 文字。",
                StatusLabel = "Auto",
                ForceSingleModel = true
            };

            try
            {
                var r = await _executeWithFallbackAsync(node, briefPrompt, briefDecision, null, false, ct);
                return r.IsSuccess ? (r.Text ?? "").Trim() : "";
            }
            catch (OperationCanceledException) { throw; }
            catch { return ""; }
        }

        private async Task<AiFallbackExecutionResult> GenerateImageFile(
            NodeControl node,
            AgentDefinition runtimeAgent,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            CancellationToken ct)
        {
            orchestration.MarkRunning("generate_image");

            string rawRequest = (userInput ?? "").Trim();
            if (string.IsNullOrWhiteSpace(rawRequest))
            {
                orchestration.MarkFailed("generate_image", "圖片描述為空。");
                return execution;
            }

            // 直接把（可能夾帶上游搜尋結果的）整段文字丟給圖片模型，它「讀不懂」而會捏造假商標 / 假公司
            // （例如憑空生出一個品牌形象牆）。先用 Claude 把內容萃取成具體、可畫的圖片提示，並禁止虛構文字 / logo。
            string prompt = await BuildImageBriefAsync(node, rawRequest, ct);
            if (string.IsNullOrWhiteSpace(prompt))
                prompt = rawRequest; // 退回原始輸入，至少還能出圖

            OpenAIImageService.ImageResult image;
            try
            {
                var imageService = new OpenAIImageService("gpt-image-2");
                image = await imageService.GenerateAsync(prompt, "1024x1024", ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                orchestration.MarkFailed("generate_image", ex.Message);
                return new AiFallbackExecutionResult
                {
                    IsSuccess = execution.IsSuccess,
                    Text = (execution.Text ?? "").TrimEnd() + $"\n\n⚠ 圖片生成失敗：{ex.Message}",
                    ActualModelId = execution.ActualModelId ?? "",
                    UsedFallback = execution.UsedFallback,
                    Summary = execution.Summary ?? "",
                    ErrorMessage = execution.ErrorMessage ?? "",
                    Attempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>()
                };
            }

            if (!image.Success)
            {
                orchestration.MarkFailed("generate_image", image.ErrorMessage);
                return new AiFallbackExecutionResult
                {
                    IsSuccess = execution.IsSuccess,
                    Text = (execution.Text ?? "").TrimEnd() + $"\n\n⚠ 圖片生成失敗：{image.ErrorMessage}",
                    ActualModelId = execution.ActualModelId ?? "",
                    UsedFallback = execution.UsedFallback,
                    Summary = execution.Summary ?? "",
                    ErrorMessage = execution.ErrorMessage ?? "",
                    Attempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>()
                };
            }

            string sourceSummary = string.IsNullOrWhiteSpace(image.RevisedPrompt)
                ? $"{orchestrationPlan.PipelineId} / gpt-image-2"
                : $"{orchestrationPlan.PipelineId} / gpt-image-2 / {image.RevisedPrompt}";

            var generated = GeneratedFileWriter.WriteImage(
                _main.GetGeneratedFilesDir(),
                title: ExtractReportTitle(prompt),
                content: image.PngBytes,
                sourceSummary: sourceSummary);

            workspace.Add(
                AgentWorkspaceBuilder.FromCapabilityData(
                    workspace,
                    node,
                    runtimeAgent?.Id ?? "image-agent",
                    "generated_file",
                    generated));

            if (generated.Success)
            {
                orchestration.MarkSuccess("generate_image", generated.FileName);

                string note = "\n\n已生成圖片。";

                return new AiFallbackExecutionResult
                {
                    IsSuccess = execution.IsSuccess,
                    Text = (execution.Text ?? "").TrimEnd() + note,
                    ActualModelId = execution.ActualModelId ?? "",
                    UsedFallback = execution.UsedFallback,
                    Summary = execution.Summary ?? "",
                    ErrorMessage = execution.ErrorMessage ?? "",
                    Attempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>()
                };
            }

            orchestration.MarkFailed("generate_image", generated.ErrorMessage);
            return execution;
        }

        /// <summary>
        /// Video Gen v1（多工具導演流程）：
        ///   1) Claude 當導演產出結構化影片計畫（劇本/分鏡/旁白/鏡頭/風格）——核心，永遠執行。
        ///   2) 配角工具（Flux/Midjourney 關鍵畫面、ElevenLabs 旁白、Suno 配樂）—— 缺 API 就記為略過。
        ///   3) 影片產生器：Veo 3（唯一 provider；無金鑰則只交付 Claude 計畫）。
        /// 支援取消（OperationCanceledException 往上拋）。即使 Veo API 未配置，使用者仍拿到完整 Claude 影片計畫。
        /// </summary>
        private async Task<AiFallbackExecutionResult> GenerateVideoFile(
            NodeControl node,
            AgentDefinition runtimeAgent,
            string userInput,
            AgentWorkspace workspace,
            OrchestrationPlanPayload orchestrationPlan,
            AiFallbackExecutionResult execution,
            OrchestrationStateMachine orchestration,
            CancellationToken ct)
        {
            orchestration.MarkRunning("generate_video");

            string prompt = (userInput ?? "").Trim();
            if (string.IsNullOrWhiteSpace(prompt))
            {
                orchestration.MarkFailed("generate_video", "影片描述為空。");
                return execution;
            }

            string agentId = runtimeAgent?.Id ?? "video-agent";

            // 1) Claude 導演：劇本 / 分鏡 / 旁白 / 鏡頭 / 風格（核心，永遠執行）。
            node.SetLoadingHint("Claude 正在寫劇本與分鏡");
            VideoPlanPayload plan = await BuildVideoPlanAsync(node, prompt, targetSeconds: 8, ct);
            node.SetLoadingHint(null);

            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "劇本/分鏡/旁白/鏡頭", "Claude", VideoProviderRoleStatus.Completed,
                $"{plan.Scenes?.Count ?? 0} 個鏡頭"));

            // 關鍵畫面 / 風格：Claude 已產出 keyframe prompt 與風格定義；實際出圖為配角。
            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "關鍵畫面/風格", "Flux / Midjourney", VideoProviderRoleStatus.SkippedNoApi,
                "Claude 已產出 keyframe prompt 與風格定義；接上 Flux/Midjourney API 後可實際出圖"));

            // 2) 配角：旁白配音（ElevenLabs）、配樂（Suno）—— 缺 API 就略過。
            var narration = new ElevenLabsNarrationService();
            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "旁白配音", narration.ProviderName,
                narration.IsConfigured ? VideoProviderRoleStatus.Planned : VideoProviderRoleStatus.SkippedNoApi,
                narration.IsConfigured ? "已偵測到金鑰（配音接線為後續工作）" : narration.NotConfiguredReason));

            var music = new SunoMusicService();
            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "配樂", music.ProviderName,
                music.IsConfigured ? VideoProviderRoleStatus.Planned : VideoProviderRoleStatus.SkippedNoApi,
                music.IsConfigured ? "已偵測到金鑰（配樂接線為後續工作）" : music.NotConfiguredReason));

            var planItem = AgentWorkspaceBuilder.FromCapabilityData(
                workspace, node, "video-director(claude)", "video_plan", plan,
                isUserVisibleOverride: true, modelId: AiModels.Claude_Sonnet46);
            workspace.Add(planItem);

            string videoPrompt = string.IsNullOrWhiteSpace(plan.VideoPromptForGenerator)
                ? prompt
                : plan.VideoPromptForGenerator;

            // Veo 3 只接受 4–8 秒；超過就 clamp（不報錯，影片說明會寫實際秒數）。
            int seconds = Math.Clamp(
                plan.TotalDurationSeconds > 0 ? plan.TotalDurationSeconds : 4,
                4, 8);

            // 「prompt → 影片請求」artifact，全程追蹤狀態 / 進度。
            var request = new VideoRequestPayload
            {
                Prompt = videoPrompt,
                DurationSeconds = seconds,
                Size = "720x1280",
                Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Queued)
            };
            var requestItem = AgentWorkspaceBuilder.FromCapabilityData(
                workspace, node, agentId, "video_request", request, isUserVisibleOverride: true);
            workspace.Add(requestItem);

            // 3) 影片產生器：Veo 3（唯一 provider）。
            var veo = new VeoVideoService();

            void OnProgress(int percent, VideoGenerationStatus status)
            {
                request.ProgressPercent = percent;
                request.PollCount++;
                request.Status = VideoGenerationStatusText.ToStorageValue(status);
                node.SetLoadingHint($"影片{VideoGenerationStatusText.ToLabel(status)} {percent}%");
            }

            byte[]? mp4 = null;
            string videoError = "";
            string providerLabel;
            string jobRef = "";
            bool attempted = false;

            try
            {
                if (veo.IsConfigured)
                {
                    attempted = true;
                    providerLabel = "Veo 3";
                    request.Model = veo.Model;
                    request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Generating);

                    var r = await veo.GenerateAsync(videoPrompt, seconds, "9:16", OnProgress, ct);
                    if (r.Success) { mp4 = r.Mp4Bytes; jobRef = r.OperationName; }
                    else videoError = r.ErrorMessage;
                }
                else
                {
                    providerLabel = "Veo 3";
                }
            }
            catch (OperationCanceledException)
            {
                request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Canceled);
                requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
                node.SetLoadingHint(null);
                orchestration.MarkFailed("generate_video", "已取消");
                throw;
            }
            finally
            {
                node.SetLoadingHint(null);
            }

            request.JobId = jobRef;

            // 把 Veo 3 model id 補回 requestItem，讓決策窗模型欄看得到。
            if (!string.IsNullOrWhiteSpace(request.Model))
                requestItem.ModelId = request.Model;

            // 3a) 影片模型皆休眠：只交付 Claude 計畫（仍算成功，因為導演計畫已產出）。
            if (!attempted)
            {
                plan.ProviderRoles.Add(VideoProviderRole.Of(
                    "影片", "Veo 3", VideoProviderRoleStatus.SkippedNoApi, veo.NotConfiguredReason));
                request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Failed);
                request.ErrorMessage = veo.NotConfiguredReason;
                planItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(plan);
                requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
                orchestration.MarkSuccess("generate_video", "已產出 Claude 影片計畫（影片模型休眠）");
                return ReplaceExecutionText(execution,
                    BuildVideoPlanNote(plan) +
                    $"\n\nℹ 影片模型休眠：{veo.NotConfiguredReason} 啟用 Veo 3 後即可依此計畫生成影片。");
            }

            // 3b) 嘗試了但失敗。
            if (mp4 == null || mp4.Length == 0)
            {
                plan.ProviderRoles.Add(VideoProviderRole.Of(
                    "影片", providerLabel, VideoProviderRoleStatus.Failed, videoError));
                request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Failed);
                request.ErrorMessage = videoError;
                planItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(plan);
                requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
                orchestration.MarkFailed("generate_video", videoError);
                return ReplaceExecutionText(execution,
                    BuildVideoPlanNote(plan) + $"\n\n⚠ 影片生成失敗（{providerLabel}）：{videoError}");
            }

            // 3c) 成功 → 寫檔。
            var generated = GeneratedFileWriter.WriteVideo(
                _main.GetGeneratedFilesDir(),
                title: string.IsNullOrWhiteSpace(plan.Title) ? ExtractReportTitle(prompt) : plan.Title,
                content: mp4,
                sourceSummary: $"{orchestrationPlan.PipelineId} / {providerLabel} / {request.Model}");

            workspace.Add(
                AgentWorkspaceBuilder.FromCapabilityData(
                    workspace, node, agentId, "generated_file", generated));

            if (generated.Success)
            {
                plan.ProviderRoles.Add(VideoProviderRole.Of(
                    "影片", providerLabel, VideoProviderRoleStatus.Completed, generated.FileName));
                request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Completed);
                request.ProgressPercent = 100;
                request.FilePath = generated.FilePath;
                planItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(plan);
                requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
                orchestration.MarkSuccess("generate_video", generated.FileName);
                return ReplaceExecutionText(execution, BuildVideoPlanNote(plan) + "\n\n✅ 影片已生成：" + generated.FileName);
            }

            plan.ProviderRoles.Add(VideoProviderRole.Of(
                "影片", providerLabel, VideoProviderRoleStatus.Failed, generated.ErrorMessage));
            request.Status = VideoGenerationStatusText.ToStorageValue(VideoGenerationStatus.Failed);
            request.ErrorMessage = generated.ErrorMessage;
            planItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(plan);
            requestItem.TextSummary = AgentWorkspaceBuilder.BuildTextSummary(request);
            orchestration.MarkFailed("generate_video", generated.ErrorMessage);
            return ReplaceExecutionText(execution,
                BuildVideoPlanNote(plan) + $"\n\n⚠ 影片寫檔失敗：{generated.ErrorMessage}");
        }

        // Presentation v1.5（Gamma / NotebookLM 之前的「夠用」占位）：兩段式作者流程
        // ——研究（Perplexity）→ 選定生成器（Claude / GPT）一次撰寫結構化 JSON deck。
        // 刻意保持簡單：內容品質的真正投資留到接上 Gamma / NotebookLM，這層不做逐頁深寫等重工。
        // 任一步失敗回 null，呼叫端 fallback 回確定性切段。
        private async Task<PresentationOutlinePayload?> BuildAuthoredPresentationAsync(
            NodeControl node,
            string userInput,
            AgentWorkspace workspace,
            AiFallbackExecutionResult execution,
            int requestedSlides,
            OrchestrationPlanPayload orchestrationPlan,
            AgentDefinition? runtimeAgent,
            CancellationToken ct)
        {
            try
            {
                var engine = _main.GetPresentationEngine();
                string authorModelId = PresentationEngineHelper.ToAuthorModelId(engine);
                string engineName = PresentationEngineHelper.ToDisplayName(engine);

                // 1) 研究（Perplexity）：best-effort，失敗 / 無金鑰就用空素材，作者改用自身知識。
                node.SetLoadingHint("Perplexity 正在查資料");
                string research = await ResearchForPresentationAsync(node, userInput, requestedSlides, ct);

                // 2) 作者（選定生成器，必要時 fallback）：一次產出結構化 JSON deck。
                node.SetLoadingHint($"{engineName} 正在撰寫簡報內容");
                string authorPrompt = PresentationAuthor.BuildAuthorPrompt(
                    userInput, research, execution.Text, requestedSlides);

                var authored = await RunAuthorStepAsync(
                    node, authorPrompt, authorModelId, $"Presentation Author ({engineName})", ct);
                if (!authored.IsSuccess || string.IsNullOrWhiteSpace(authored.Text))
                    return null;

                node.SetLoadingHint(null);

                // 模型 id 帶上實際作者模型，決策窗才看得到「簡報是誰寫的」（Claude / GPT → 成功；pplx → 退回切段）。
                return PresentationAuthor.Parse(
                    authored.Text,
                    userInput,
                    requestedSlides,
                    orchestrationPlan.PipelineId,
                    string.IsNullOrWhiteSpace(authored.ActualModelId) ? authorModelId : authored.ActualModelId,
                    runtimeAgent?.Id ?? "presentation-agent");
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return null;
            }
        }

        // §7.2 單張投影片重生：用作者模型只重寫指定那一張，回傳替換後的新大綱（失敗回 null，呼叫端保留原樣）。
        public async Task<PresentationOutlinePayload?> RegeneratePresentationSlideAsync(
            NodeControl node,
            PresentationOutlinePayload outline,
            int slideOrder,
            string userInput,
            CancellationToken ct)
        {
            if (outline == null)
                return null;

            try
            {
                var engine = _main.GetPresentationEngine();
                string authorModelId = PresentationEngineHelper.ToAuthorModelId(engine);
                string engineName = PresentationEngineHelper.ToDisplayName(engine);

                node.SetLoadingHint($"{engineName} 正在重生第 {slideOrder} 張投影片");
                string prompt = PresentationAuthor.BuildSingleSlidePrompt(outline, slideOrder, userInput);

                var authored = await RunAuthorStepAsync(
                    node, prompt, authorModelId, $"Slide Regen ({engineName})", ct);

                node.SetLoadingHint(null);

                if (!authored.IsSuccess || string.IsNullOrWhiteSpace(authored.Text))
                    return null;

                var parsed = PresentationAuthor.ParseSingleSlide(authored.Text);
                if (parsed == null)
                    return null;

                return PresentationAuthor.ReplaceSlide(
                    outline, slideOrder, parsed.Value.Heading, parsed.Value.Bullets);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                node.SetLoadingHint(null);
                return null;
            }
        }

        // 內容作者共用 executor：以選定的作者模型跑一步（簡報骨架 / 逐頁內容 / 報告 / 表格），必要時 fallback。
        private async Task<AiFallbackExecutionResult> RunAuthorStepAsync(
            NodeControl node, string prompt, string authorModelId, string label, CancellationToken ct)
        {
            var decision = new NodeExecutionDecision
            {
                RequestedAgentId = "general-agent",
                ActualAgentId = "general-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(authorModelId),
                ModelId = AiModelHelper.NormalizeNodeModel(authorModelId),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = label,
                ResolverReason = label,
                StatusLabel = "Auto",
                ForceSingleModel = false
            };

            return await _executeWithFallbackAsync(node, prompt, decision, null, false, ct);
        }

        // §6 報告 / 表格內容作者：用作者模型把主答案整理成乾淨內容，回傳去圍欄後的文字（失敗回 null）。
        private async Task<string?> AuthorCleanAsync(
            NodeControl node, string prompt, string label, CancellationToken ct)
        {
            try
            {
                string modelId = PresentationEngineHelper.ToAuthorModelId(_main.GetPresentationEngine());
                var r = await RunAuthorStepAsync(node, prompt, modelId, label, ct).ConfigureAwait(false);
                if (!r.IsSuccess || string.IsNullOrWhiteSpace(r.Text))
                    return null;
                return DocumentAuthor.StripCodeFence(r.Text);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        // 用 Perplexity 蒐集簡報素材；無金鑰 / 失敗時回空字串（不中斷，作者改用自身知識）。
        private async Task<string> ResearchForPresentationAsync(
            NodeControl node, string userInput, int requestedSlides, CancellationToken ct)
        {
            try
            {
                string researchPrompt = PresentationAuthor.BuildResearchPrompt(userInput, requestedSlides);

                var researchDecision = new NodeExecutionDecision
                {
                    RequestedAgentId = "general-agent",
                    ActualAgentId = "general-agent",
                    RequestedModelId = AiModelHelper.NormalizeNodeModel(AiModels.Perplexity_Sonar),
                    ModelId = AiModelHelper.NormalizeNodeModel(AiModels.Perplexity_Sonar),
                    ActualModelId = "",
                    TaskMode = NodeTaskMode.Research,
                    ResolverLabel = "Presentation Research (Perplexity)",
                    ResolverReason = "用 Perplexity 取得簡報所需的即時事實與數據。",
                    StatusLabel = "Auto",
                    ForceSingleModel = false
                };

                var r = await _executeWithFallbackAsync(node, researchPrompt, researchDecision, null, false, ct);
                return r.IsSuccess ? (r.Text ?? "") : "";
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return "";
            }
        }

        private async Task<VideoPlanPayload> BuildVideoPlanAsync(
            NodeControl node, string prompt, int targetSeconds, CancellationToken ct)
        {
            string directorPrompt = VideoPlanBuilder.BuildDirectorPrompt(prompt, targetSeconds);

            var directorDecision = new NodeExecutionDecision
            {
                RequestedAgentId = "general-agent",
                ActualAgentId = "general-agent",
                RequestedModelId = AiModelHelper.NormalizeNodeModel(AiModels.Claude_Sonnet46),
                ModelId = AiModelHelper.NormalizeNodeModel(AiModels.Claude_Sonnet46),
                ActualModelId = "",
                TaskMode = NodeTaskMode.Chat,
                ResolverLabel = "Video Director (Claude)",
                ResolverReason = "Claude 產出影片劇本 / 分鏡 / 旁白 / 鏡頭計畫。",
                StatusLabel = "Auto",
                ForceSingleModel = true
            };

            try
            {
                var r = await _executeWithFallbackAsync(node, directorPrompt, directorDecision, null, false, ct);
                return VideoPlanBuilder.Parse(r.IsSuccess ? r.Text : "", prompt, targetSeconds);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return VideoPlanBuilder.Parse("", prompt, targetSeconds);
            }
        }

        private static string BuildVideoPlanNote(VideoPlanPayload plan)
        {
            var sb = new System.Text.StringBuilder();
            sb.Append("\n\n【影片計畫（Claude 導演）】");

            if (!string.IsNullOrWhiteSpace(plan.Title))
                sb.Append($"\n標題：{plan.Title}");
            if (!string.IsNullOrWhiteSpace(plan.Logline))
                sb.Append($"\n概念：{plan.Logline}");
            if (!string.IsNullOrWhiteSpace(plan.StyleDefinition))
                sb.Append($"\n風格：{plan.StyleDefinition}");

            sb.Append($"\n分鏡：{plan.Scenes?.Count ?? 0} 個鏡頭，約 {plan.TotalDurationSeconds} 秒");

            sb.Append("\n工具分工：");
            foreach (var role in plan.ProviderRoles)
                sb.Append($"\n· {role.Role}：{role.Provider}（{VideoProviderRoleStatus.ToLabel(role.Status)}）");

            return sb.ToString();
        }

        // 影片 / 報告 / 表格等產出型任務：主模型的回答直接取代（不要夾在「我不能生成影片」後面）。
        private static AiFallbackExecutionResult ReplaceExecutionText(
            AiFallbackExecutionResult execution, string text)
        {
            return new AiFallbackExecutionResult
            {
                IsSuccess = execution.IsSuccess,
                Text = text,
                ActualModelId = execution.ActualModelId ?? "",
                UsedFallback = execution.UsedFallback,
                Summary = execution.Summary,
                ErrorMessage = execution.ErrorMessage,
                Attempts = execution.Attempts
            };
        }

        private static AiFallbackExecutionResult AppendExecutionNote(
            AiFallbackExecutionResult execution, string note)
        {
            return new AiFallbackExecutionResult
            {
                IsSuccess = execution.IsSuccess,
                Text = (execution.Text ?? "").TrimEnd() + note,
                ActualModelId = execution.ActualModelId ?? "",
                UsedFallback = execution.UsedFallback,
                Summary = execution.Summary ?? "",
                ErrorMessage = execution.ErrorMessage ?? "",
                Attempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>()
            };
        }

        private async Task<AiFallbackExecutionResult> RunFinalSynthesisAsync(
            NodeControl node,
            AgentDefinition rootAgent,
            string originalInput,
            AgentWorkspace workspace,
            NodeExecutionDecision rootDecision,
            string preferenceBlock,
            CancellationToken ct)
        {
            var synthesizer = AgentRegistry.Get("general-agent");

            var synthesisDecision = new NodeExecutionDecision
            {
                RequestedAgentId = synthesizer.Id,
                ActualAgentId = synthesizer.Id,
                RequestedModelId = AiModelHelper.NormalizeNodeModel(synthesizer.DefaultModelId),
                ModelId = AiModelHelper.NormalizeNodeModel(synthesizer.DefaultModelId),
                ActualModelId = "",
                TaskMode = rootDecision.TaskMode,
                ResolverLabel = "Final Synthesizer",
                ResolverReason = "Parallel agents completed; general-agent synthesizes workspace outputs.",
                StatusLabel = "Auto",
                ForceSingleModel = true
            };

            string workspaceBlock = workspace.BuildPromptBlock();

            System.Diagnostics.Debug.WriteLine($"[Workspace] Count={workspace.GetAll().Count}");
            System.Diagnostics.Debug.WriteLine(
                $"[Workspace Preview]\n{workspaceBlock.Substring(0, Math.Min(1000, workspaceBlock.Length))}");

            bool hasVerifiedFacts = workspace.GetByType("verified_facts").Count > 0;
            bool isFinanceTask = FinanceTaskDetector.IsFinanceFocused(originalInput);
            string synthesisInstructions = hasVerifiedFacts && isFinanceTask
                ? BuildFinanceFinalSynthesisInstructions()
                : BuildGeneralFinalSynthesisInstructions();

            string prefHeader = string.IsNullOrWhiteSpace(preferenceBlock)
                ? ""
                : preferenceBlock.Trim() +
                  "\n（以上偏好為最高優先，蓋過下方任何「使用繁體中文」等預設規則；若偏好指定輸出語言或格式，最終答案必須改用該語言與格式。）\n\n";

            string synthesisInput =
        $@"{prefHeader}你是 final synthesizer。你的任務是把 shared workspace 整理成使用者真正需要看的最終答案。

【使用者原始任務】
{originalInput}

【Shared Workspace】
{workspaceBlock}

{synthesisInstructions}

請現在輸出最終答案：";

            return await _executeWithFallbackAsync(
                node,
                synthesisInput,
                synthesisDecision,
                null,
                false,
                ct);
        }

        private void ApplyAutoCostPolicyToDecision(NodeExecutionDecision decision)
        {
            if (decision == null || !_main.IsAutoModelSelectionEnabled())
                return;

            string requested = AiModelHelper.NormalizeNodeModel(decision.ModelId);
            // §15 個人化：成本降級的唯一依據＝使用者的個人化開關。
            // 只有使用者明確關閉該高成本模型時才降級；未關閉就原樣保留。
            if (!AiAutoCostPolicy.TryEnforceUserBlock(requested, out string resolved))
                return;

            decision.ModelId = resolved;
            decision.CapabilityResolvedModelId = resolved;
            decision.CapabilityAdjusted = true;

            string costReason = $"Auto cost policy: {requested} → {resolved}";
            decision.CapabilityReason = string.IsNullOrWhiteSpace(decision.CapabilityReason)
                ? costReason
                : decision.CapabilityReason + " / " + costReason;

            decision.ResolverReason = string.IsNullOrWhiteSpace(decision.ResolverReason)
                ? costReason
                : decision.ResolverReason + " / " + costReason;

            if (!string.IsNullOrWhiteSpace(decision.ResolverLabel) &&
                !decision.ResolverLabel.Contains("Auto Cost Policy", StringComparison.OrdinalIgnoreCase))
            {
                decision.ResolverLabel += " + Auto Cost Policy";
            }
        }

        private async Task<AiFallbackExecutionResult?> TryRepairInvalidCodeDiffAsync(
            NodeControl node,
            string originalFinalInput,
            string userGoal,
            AiFallbackExecutionResult previousExecution,
            CodeDiffArtifactPayload invalidDiff,
            CodeDiffValidationPayload validation,
            NodeExecutionDecision decision,
            CancellationToken ct)
        {
            if (invalidDiff == null || validation == null)
                return null;

            string repairInput = BuildInvalidCodeDiffRepairPrompt(
                originalFinalInput,
                userGoal,
                previousExecution?.Text ?? "",
                invalidDiff,
                validation);

            var repairDecision = new NodeExecutionDecision
            {
                RequestedAgentId = decision.ActualAgentId,
                ActualAgentId = decision.ActualAgentId,
                RequestedModelId = decision.ModelId,
                ModelId = decision.ModelId,
                ActualModelId = "",
                TaskMode = decision.TaskMode,
                ResolverLabel = "Code Diff Repair",
                ResolverReason = "Previous model-generated diff failed dry-run validation; requesting one repair attempt.",
                StatusLabel = decision.StatusLabel,
                ForceSingleModel = decision.ForceSingleModel,
                UseStreaming = false
            };

            var repaired = await _executeWithFallbackAsync(
                node,
                repairInput,
                repairDecision,
                null,
                false,
                ct);

            repaired = FinalAnswerSanitizer.Sanitize(
                repaired,
                enforceSynthesisFormat: false);

            if (string.IsNullOrWhiteSpace(repaired.Text))
                return null;

            return repaired;
        }

        private static AiFallbackExecutionResult WithSafeInvalidPatchMessage(
            AiFallbackExecutionResult execution,
            CodeDiffValidationPayload validation)
        {
            var lines = new List<string>
            {
                "我找到可能的修改方向，但產生的 patch 沒有通過 dry-run validation，因此目前不應套用。",
                "",
                "驗證結果：",
                validation?.Summary ?? "Diff validation failed."
            };

            foreach (var file in validation?.Files ?? Array.Empty<CodeDiffValidationFileResult>())
            {
                if (file == null)
                    continue;

                lines.Add($"- {file.Path}: {file.Message}");
            }

            lines.Add("");
            lines.Add("建議下一步：改用更小範圍的修正請求，或讓系統先列出可確認的 bug，再逐項產生 patch。");

            return new AiFallbackExecutionResult
            {
                IsSuccess = execution.IsSuccess,
                Text = string.Join(Environment.NewLine, lines),
                ActualModelId = execution.ActualModelId,
                UsedFallback = execution.UsedFallback,
                Summary = execution.Summary,
                ErrorMessage = execution.ErrorMessage,
                Attempts = execution.Attempts
            };
        }

        private static string BuildInvalidCodeDiffRepairPrompt(
            string originalFinalInput,
            string userGoal,
            string previousOutput,
            CodeDiffArtifactPayload invalidDiff,
            CodeDiffValidationPayload validation)
        {
            var validationLines = new List<string>();

            if (!string.IsNullOrWhiteSpace(validation.Summary))
                validationLines.Add(validation.Summary);

            foreach (var file in validation.Files ?? Array.Empty<CodeDiffValidationFileResult>())
            {
                if (file == null)
                    continue;

                validationLines.Add(
                    $"{file.Status}: {file.Path} (+{file.AddedLines}/-{file.RemovedLines}) - {file.Message}");
            }

            foreach (var message in validation.Messages ?? Array.Empty<string>())
            {
                if (!string.IsNullOrWhiteSpace(message))
                    validationLines.Add(message);
            }

            return
$@"你剛剛輸出的 unified diff 沒有通過 dry-run validation。請只做一次修復，重新輸出一份可以對上附件 snapshot 的 unified diff。

【使用者目標】
{TrimForPrompt(userGoal, 1200)}

【原始任務摘要】
{BuildCompactOriginalTaskSummary(originalFinalInput)}

【上一版輸出】
{TrimForPrompt(previousOutput, MaxRepairPreviousOutputChars)}

【上一版 diff】
```diff
{TrimForPrompt(invalidDiff.UnifiedDiff, MaxRepairDiffChars)}
```

【Validation 失敗原因】
{string.Join("\n", validationLines)}

【修復規則】
1. 必須根據附件 snapshot 內實際存在的原始碼行產生 diff。
2. 不要使用 validation 已指出找不到的 source line。
3. 必須輸出 fenced unified diff，格式為 ```diff。
4. 不要宣稱已套用檔案。
5. 如果無法安全修復，請直接說明無法修復的原因，不要輸出假 diff。";
        }

        private static bool IsLargeBroadCodePatchRequest(
            string userGoal,
            IReadOnlyDictionary<string, object> capabilityData,
            AgentWorkspace workspace)
        {
            if (!IsBroadCodePatchGoal(userGoal))
                return false;

            var snapshot = capabilityData.TryGetValue("code_file_snapshot", out var snapshotValue)
                ? snapshotValue as CodeFileSnapshotPayload
                : workspace.GetByType("code_file_snapshot")
                    .Select(x => x.Payload as CodeFileSnapshotPayload)
                    .FirstOrDefault(x => x != null);

            if (snapshot?.Files == null || snapshot.Files.Count == 0)
                return false;

            int totalChars = snapshot.Files
                .Where(x => x != null)
                .Sum(x => x.CharacterCount);

            int totalLines = snapshot.Files
                .Where(x => x != null)
                .Sum(x => x.LineCount);

            return totalChars >= 30000 || totalLines >= 700;
        }

        private static bool IsBroadCodePatchGoal(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string normalized = text.Trim().ToLowerInvariant();

            return ContainsAnyInvariant(normalized,
                "把看到的 bug 都修好",
                "看到的 bug 都修好",
                "看到的bug都修好",
                "所有 bug",
                "all bugs",
                "fix all",
                "fix every",
                "全部修好",
                "都修好",
                "全面修",
                "整份",
                "整個程式",
                "whole file",
                "entire file");
        }

        private static string BuildLargeBroadCodePatchGuardMessage(
            string userGoal,
            IReadOnlyDictionary<string, object> capabilityData,
            AgentWorkspace workspace)
        {
            var snapshot = capabilityData.TryGetValue("code_file_snapshot", out var snapshotValue)
                ? snapshotValue as CodeFileSnapshotPayload
                : workspace.GetByType("code_file_snapshot")
                    .Select(x => x.Payload as CodeFileSnapshotPayload)
                    .FirstOrDefault(x => x != null);

            var files = snapshot?.Files?
                .Where(x => x != null)
                .Take(5)
                .Select(x => $"- {x.FileName}：{x.LineCount} 行 / {x.CharacterCount} 字元")
                .ToList() ?? new List<string>();

            string fileText = files.Count == 0
                ? "- 已偵測到大型程式附件。"
                : string.Join(Environment.NewLine, files);

            return
$@"這個請求目前先不直接產生 patch，因為它是大型檔案的全檔泛修任務，直接丟給模型會很貴，而且容易產生無法套用的假 diff。

已建立附件快照：
{fileText}

建議下一步：
1. 先要求「列出可疑 bug 與對應方法/行號」，不產生 patch。
2. 選其中一個 bug 或一個方法，再要求產生 unified diff。
3. 系統會對該小範圍 patch 做 dry-run validation，通過後再顯示為可用 diff。

目前不應對這種任務使用「把看到的 bug 都修好」的一次性全檔 patch 流程，否則會繼續浪費 token 並得到不可靠結果。";
        }

        private static bool ContainsAnyInvariant(string text, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(text) || needles == null || needles.Length == 0)
                return false;

            foreach (var needle in needles)
            {
                if (!string.IsNullOrWhiteSpace(needle) &&
                    text.Contains(needle.ToLowerInvariant(), StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string TrimForPrompt(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            string trimmed = text.Trim();
            return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max) + "...";
        }

        private static string BuildCompactOriginalTaskSummary(string? originalFinalInput)
        {
            if (string.IsNullOrWhiteSpace(originalFinalInput))
                return "原始任務內容未提供。";

            var lines = originalFinalInput
                .Split(new[] { "\r\n", "\n" }, StringSplitOptions.None)
                .Where(line =>
                    !line.Contains("Content:", StringComparison.OrdinalIgnoreCase) &&
                    !line.StartsWith("```", StringComparison.Ordinal) &&
                    !line.Contains("import ", StringComparison.Ordinal) &&
                    !line.Contains("public class ", StringComparison.Ordinal) &&
                    !line.Contains("private ", StringComparison.Ordinal) &&
                    !line.Contains("protected ", StringComparison.Ordinal) &&
                    !line.Contains("void ", StringComparison.Ordinal))
                .Take(80);

            var compact = string.Join(Environment.NewLine, lines).Trim();
            if (string.IsNullOrWhiteSpace(compact))
                return "原始任務包含大型附件 snapshot。為控制 token，repair 階段不重貼完整原始碼；請只根據上一版 diff 與 validation 失敗原因修正 patch。";

            return TrimForPrompt(compact, 3000) +
                   "\n\n注意：完整附件 snapshot 保留在 workspace 供 dry-run validation 使用；repair prompt 不重貼完整原始碼，以避免大型檔案重複消耗 token。";
        }

        private static string BuildFinanceFinalSynthesisInstructions()
        {
            return
@"【最高優先規則】
1. 如果 Shared Workspace 中有【Verified Facts】，必須優先使用它作為價格、日期、財報、EPS、營收、毛利率、指引等事實來源。
2. 如果沒有【Verified Facts】，但有【Search Context】或 research-agent 的 search_summary，才可使用 search_summary 作為事實來源。
3. 只有在 Shared Workspace 完全沒有 Verified Facts、Search Context、search_summary 時，才可以回答資料不足。
4. Fact ownership 是硬規則：
   - UsageRole=numeric_fact_source 才能作為價格、日期、財報、EPS、營收、毛利率、指引等數字來源。
   - UsageRole=background_context 只能作為背景，不可覆蓋 numeric_fact_source。
   - UsageRole=analysis_only、Analysis Context、reasoning_analysis、parallel_agent_output、delegate_output 只能用於推論與整理，不可新增或覆蓋任何事實數字。
   - OwnerAgent=research-agent 且 OwnerCapability=search-capability 的 numeric facts 是事實擁有者。
5. Authority ranking 是採用順序：official > market_quote > trusted_news > search_context > model_generated。若同一 numeric fact 有衝突，採用 AuthorityRank 較高者；低權威來源只能作為補充或列入資料衝突。
6. Analysis Context、reasoning_analysis、parallel_agent_output、delegate_output 只能用於推論與整理，不可新增或覆蓋任何事實數字。
7. 若同一項資料有多個來源數字：
   - regular close、after-hours、pre-market 屬於不同交易時段，不可互相視為資料衝突；請分開標示。
   - 若 verified_facts 來自 official earnings / official facts repair，且 Search Context 或舊搜尋摘要有不同數字，應以 verified_facts 為準，不要把被 repair 取代的舊數字列為資料衝突。
   - 若數字接近，請合併成簡短區間或代表值。
   - 若數字明顯衝突，請列在「資料衝突」中。
   - 不要把所有來源逐條原封不動列出。
8. 若某個 ticker 只有部分欄位缺失，只能標示該欄位缺失；不可因此整體回答「財報核心數字不足」或「資料不足」。
9. 不要使用「資料批次」「較強的那組資料」「若採用某組資料」這類內部研究口徑；請直接使用最高權威 verified_facts。
10. 不可輸出內部標記，例如 Agent Workspace、Task Plan、Search Summary、Verified Facts、Search Context、Analysis Context、parallel_agent_output、delegate_output。
11. 不可輸出 citation marker，例如 [1][2][3]。
12. 不要寫成研究紀錄，不要把 workspace 全部倒出來。請輸出給一般使用者看的精簡結論。
13. 預設使用繁體中文；若提供了【使用者偏好】並指定輸出語言，必須改用該偏好指定的語言。

【輸出格式】
請嚴格使用以下格式。只針對使用者詢問的標的輸出，不得自行加入未被詢問的股票或比較項目：

結論
- 用 2～4 點直接回答使用者任務。
- 針對每個被詢問的標的給出短期判斷。
- 若使用者要求比較多個標的，直接說哪個較穩、哪個彈性較大、哪個風險較高。
- 若使用者只詢問單一標的，只回答該標的，不要加入其他標的的「資料不足」備注。

關鍵資料
- 只列被詢問標的最重要的股價、財報或市場資料，每個標的最多 5 點。
- 如果資料來源衝突，不要在這裡展開；只簡短標示「報價來源有衝突，詳見資料衝突」。

短期走勢判斷
- 只針對被詢問的標的，每個標的最多 1 段。
- 必須清楚區分「已知資料」與「合理推論」。
- 不要保證漲跌。

資料衝突 / 缺失
- 只列真正影響判斷的衝突或缺失。
- 若沒有重大衝突，就寫「目前沒有影響主要判斷的重大缺口」。
- 不要把所有來源重複列一遍。

總結一句話
- 用一句話收束。

【風格限制】
- 不要長篇列點。
- 不要重複同一個觀點。
- 不要把每個來源都展開。
- 不要用「已知資料 / 合理推論 / 風險」這三段舊格式。
- 最終答案長度控制在一般回答可讀範圍內。";
        }

        private static string BuildGeneralFinalSynthesisInstructions()
        {
            return
@"【最高優先規則】
1. 依照使用者原始任務回答，不要套用金融、股票、短期走勢、資料衝突等格式，除非使用者明確要求。
2. Shared Workspace 只作為背景與附件內容來源；不可輸出內部標記，例如 Agent Workspace、Task Plan、Search Summary、Code File Snapshot、parallel_agent_output、delegate_output。
3. 若使用者要求一句話，就只輸出一句話。
4. 若任務是程式摘要或檔案說明，直接說明檔案用途、主要功能與關鍵結構；不要加入不適用的金融段落。
5. 不可輸出 citation marker，例如 [1][2][3]。
6. 預設使用繁體中文；若提供了【使用者偏好】並指定輸出語言，必須改用該偏好指定的語言。

【風格限制】
- 簡潔、直接，避免把 workspace 逐條倒出來。
- 尊重使用者指定的長度、語氣與格式。";
        }
        private static string BuildCapabilityDataBlock(
            IReadOnlyDictionary<string, object> capabilityData)
        {
            if (capabilityData == null || capabilityData.Count == 0)
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("【Capability Data】");
            sb.AppendLine("以下內容來自系統工具/能力層的真實輸出，屬高優先參考。");
            sb.AppendLine("回答時請優先使用這些資料，不要忽略，也不要憑空改寫來源。");

            bool hasVerifiedFacts = capabilityData.ContainsKey("verified_facts");

            string codeUserGoal =
                (capabilityData.TryGetValue("code_analysis", out var caVal) && caVal is CodeAnalysisPayload caPayload)
                    ? caPayload.UserGoal ?? ""
                    : "";

            foreach (var kv in capabilityData)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                    continue;

                if (string.Equals(kv.Key, "search_summary", StringComparison.OrdinalIgnoreCase))
                {
                    if (hasVerifiedFacts)
                    {
                        sb.AppendLine();
                        sb.AppendLine("【Search Summary Omitted】");
                        sb.AppendLine("Structured verified_facts are present. Search summary text is intentionally omitted so older or lower-authority numeric snippets cannot override verified facts.");
                        continue;
                    }

                    AppendSearchSummary(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "file_summary", StringComparison.OrdinalIgnoreCase))
                {
                    AppendFileSummary(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "code_analysis", StringComparison.OrdinalIgnoreCase))
                {
                    AppendCodeAnalysis(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "code_file_snapshot", StringComparison.OrdinalIgnoreCase))
                {
                    AppendCodeFileSnapshot(sb, kv.Value, codeUserGoal);
                    continue;
                }

                if (string.Equals(kv.Key, "code_diff_draft", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(kv.Key, "code_diff", StringComparison.OrdinalIgnoreCase))
                {
                    AppendCodeDiffArtifact(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "task_plan", StringComparison.OrdinalIgnoreCase))
                {
                    AppendTaskPlan(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "reasoning_analysis", StringComparison.OrdinalIgnoreCase))
                {
                    AppendReasoningAnalysis(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "verified_facts", StringComparison.OrdinalIgnoreCase))
                {
                    AppendVerifiedFacts(sb, kv.Value);
                    continue;
                }

                if (string.Equals(kv.Key, "search_results", StringComparison.OrdinalIgnoreCase))
                {
                    AppendSearchResults(sb, kv.Value);
                    continue;
                }
                sb.AppendLine();
                sb.AppendLine($"【{kv.Key}】");
                sb.AppendLine(kv.Value.ToString() ?? "");
            }

            return sb.ToString().Trim();
        }

        private static void AppendVerifiedFacts(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not VerifiedFactPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Verified Facts】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Verified Facts】");
            sb.AppendLine("以下資料是唯一可用來回答數字、價格、日期、財報、即時資訊的事實來源。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);

            if (payload.Facts == null || payload.Facts.Count == 0)
                return;

            foreach (var fact in payload.Facts)
            {
                if (fact == null)
                    continue;

                sb.AppendLine($"- {fact.Subject} / {fact.FactType}: {fact.Value} {fact.Unit}".Trim());

                if (!string.IsNullOrWhiteSpace(fact.AsOf))
                    sb.AppendLine($"  AsOf: {fact.AsOf}");

                if (!string.IsNullOrWhiteSpace(fact.SourceTitle))
                    sb.AppendLine($"  Source: {fact.SourceTitle}");

                if (!string.IsNullOrWhiteSpace(fact.SourceUrl))
                    sb.AppendLine($"  Url: {fact.SourceUrl}");

                if (!string.IsNullOrWhiteSpace(fact.Confidence))
                    sb.AppendLine($"  Confidence: {fact.Confidence}");

                if (!string.IsNullOrWhiteSpace(fact.OwnerAgentId))
                    sb.AppendLine($"  OwnerAgent: {fact.OwnerAgentId}");

                if (!string.IsNullOrWhiteSpace(fact.OwnerCapabilityId))
                    sb.AppendLine($"  OwnerCapability: {fact.OwnerCapabilityId}");

                if (!string.IsNullOrWhiteSpace(fact.AuthorityLevel))
                    sb.AppendLine($"  Authority: {fact.AuthorityLevel} (rank {FactOwnership.AuthorityRank(fact.AuthorityLevel)})");

                if (!string.IsNullOrWhiteSpace(fact.UsageRole))
                    sb.AppendLine($"  UsageRole: {fact.UsageRole}");
            }
        }

        private static string BuildFinalOutputFormatBlock()
        {
            return
@"【Final Output Format - Required】
請使用以下五段標題，且不可省略標題：

結論
- 2～4 點直接回答。

關鍵資料
- 對每個 ticker 標清楚資料類型：收盤價、盤後價、盤前價、即時價、財報、指引。
- 若某種報價未取得，請寫「盤前價：未取得」或「即時價：未取得」，不要把收盤價當即時價。

短期走勢判斷
- 明確區分 verified facts 與合理推論。

資料衝突 / 缺失
- 只列真正衝突或缺失。不同交易時段的收盤價、盤後價、盤前價不是衝突。

總結一句話
- 用一句話收束。";
        }

        private static string BuildCodeDiffOutputInstructionBlock()
        {
            return
@"【Code Diff Output - Required When Fixing Code】
目前 workspace 中已有 code_diff_draft。若使用者要求修正、修改、重構或產生 patch，且你能根據已提供的實際原始碼內容安全產生修正，最終回答應包含一個 fenced unified diff：

```diff
diff --git a/path/to/file b/path/to/file
--- a/path/to/file
+++ b/path/to/file
@@
- old line
+ new line
```

規則：
1. diff 必須根據附件 snapshot 中的實際內容產生，不可捏造不存在的檔案。
2. 若資訊不足以安全產生 diff，請明確說明缺少什麼，不要輸出假 diff。
3. 不要宣稱已修改或已套用檔案；目前只能提出 patch。
4. 若 Code File Snapshot 顯示 PromptTruncated=True，代表你只看到低成本摘錄，不可宣稱已完整檢查整份檔案。
5. 對「把所有 bug 都修好」這類大範圍任務，若只能看到部分內容，只能提出可被摘錄內容支持的有限修正；若無法確認，請要求縮小範圍或先列候選區域。
6. 除了 diff，可以用短句說明修了什麼，但不要輸出 workspace 內部標記。";
        }

        // Code Agent v1.5：bug 盤點模式——先列清單、不出 patch，讓使用者挑選要修哪幾項。
        private static string BuildBugListingInstructionBlock()
        {
            return
@"【Bug 清單模式 - 先盤點，不要直接修】
使用者想先看到問題清單，再決定修哪一個。本次請「只列出 bug／問題清單」，不要輸出任何 diff 或 patch。

輸出格式（務必逐項編號，方便使用者挑選）：
Bug 1（嚴重度：高/中/低）｜位置：檔名:行號 或 類別.方法
- 問題：一句話描述問題與影響
- 建議：一句話說明修法方向（先不要給完整程式碼）

Bug 2（…）
…

規則：
1. 依嚴重度由高到低排序，最多列 12 項。
2. 只列出有實際根據（snapshot 內容支持）的問題，不要臆測不存在的程式碼。
3. 若內容被分段／截斷（PromptTruncated=True 或有 ChunkMap），請在清單最後說明還有哪些區段尚未檢查。
4. 結尾固定加一句：『想修正哪幾項？回覆「修正 1」或「修正 1,3」我就針對那幾項產生 patch。』";
        }

        // Code Agent v1.5：大型程式任務的成本/風險提醒（過程資訊，提醒模型保守處理）。
        private static string BuildCodeRiskNoteBlock(CodeTaskAssessmentPayload assessment)
        {
            var sb = new StringBuilder();
            sb.AppendLine("【大型程式任務提醒】");
            sb.AppendLine(
                $"任務規模：{assessment.SizeTier}；風險：{assessment.RiskLevel}；" +
                $"檔案 {assessment.FileCount} 個、約 {assessment.TotalLines} 行。");

            if (assessment.Warnings != null)
            {
                foreach (var w in assessment.Warnings)
                {
                    if (!string.IsNullOrWhiteSpace(w))
                        sb.AppendLine("- " + w);
                }
            }

            sb.AppendLine("處理原則：優先給出有把握、範圍明確的修正；不要宣稱已完整檢查整個專案。");
            return sb.ToString().TrimEnd();
        }

        private static string TryGetCodeRequestType(
            IReadOnlyDictionary<string, object> capabilityData,
            AgentWorkspace workspace)
        {
            if (capabilityData != null &&
                capabilityData.TryGetValue("code_analysis", out var v) &&
                v is CodeAnalysisPayload p)
            {
                return p.RequestType ?? "";
            }

            var ws = workspace?
                .GetByType("code_analysis")
                .Select(x => x.Payload as CodeAnalysisPayload)
                .FirstOrDefault(x => x != null);

            return ws?.RequestType ?? "";
        }

        private static CodeFileSnapshotPayload? TryGetCodeSnapshot(
            IReadOnlyDictionary<string, object> capabilityData,
            AgentWorkspace workspace)
        {
            if (capabilityData != null &&
                capabilityData.TryGetValue("code_file_snapshot", out var v) &&
                v is CodeFileSnapshotPayload p)
            {
                return p;
            }

            return workspace?
                .GetByType("code_file_snapshot")
                .Select(x => x.Payload as CodeFileSnapshotPayload)
                .FirstOrDefault(x => x != null);
        }

        private static void AppendReasoningAnalysis(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not ReasoningPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Reasoning Analysis】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Reasoning Analysis】");
            sb.AppendLine("以下為系統推論策略。回答時請遵守，但不可輸出此內部區塊名稱。");

            if (!string.IsNullOrWhiteSpace(payload.ReasoningType))
                sb.AppendLine($"Type: {payload.ReasoningType}");

            if (!string.IsNullOrWhiteSpace(payload.Basis))
                sb.AppendLine($"Basis: {payload.Basis}");

            if (payload.Inferences != null && payload.Inferences.Count > 0)
            {
                sb.AppendLine("Inference Rules:");
                foreach (var item in payload.Inferences)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                        sb.AppendLine($"- {item}");
                }
            }

            if (payload.Uncertainties != null && payload.Uncertainties.Count > 0)
            {
                sb.AppendLine("Uncertainties:");
                foreach (var item in payload.Uncertainties)
                {
                    if (!string.IsNullOrWhiteSpace(item))
                        sb.AppendLine($"- {item}");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.OutputGuidance))
                sb.AppendLine($"Output Guidance: {payload.OutputGuidance}");
        }

        private static void AppendTaskPlan(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not TaskDecompositionPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Task Plan】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Task Plan】");
            sb.AppendLine("以下是系統對複合任務的拆解。回答時請依照此順序整合工具資料，但不可把 Task Plan 標題或內部步驟原樣輸出。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);

            if (payload.Steps == null || payload.Steps.Count == 0)
                return;

            foreach (var step in payload.Steps.OrderBy(x => x.Order))
            {
                sb.AppendLine($"{step.Order}. {step.StepType}");

                if (!string.IsNullOrWhiteSpace(step.Goal))
                    sb.AppendLine($"   Goal: {step.Goal}");

                if (!string.IsNullOrWhiteSpace(step.RequiredInput))
                    sb.AppendLine($"   Required Input: {step.RequiredInput}");

                if (!string.IsNullOrWhiteSpace(step.OutputExpectation))
                    sb.AppendLine($"   Output: {step.OutputExpectation}");
            }
        }

        private static void AppendCodeAnalysis(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not CodeAnalysisPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Code Analysis】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Code Analysis】");
            sb.AppendLine("以下內容來自 code-capability 的結構化分析，回答程式問題時應優先依據此分析。");

            if (!string.IsNullOrWhiteSpace(payload.RequestType))
                sb.AppendLine($"Request Type: {payload.RequestType}");

            if (!string.IsNullOrWhiteSpace(payload.Language))
                sb.AppendLine($"Language: {payload.Language}");

            if (!string.IsNullOrWhiteSpace(payload.UserGoal))
                sb.AppendLine($"User Goal: {payload.UserGoal}");

            if (payload.DetectedSignals != null && payload.DetectedSignals.Count > 0)
            {
                sb.AppendLine("Detected Signals:");
                foreach (var signal in payload.DetectedSignals)
                {
                    if (!string.IsNullOrWhiteSpace(signal))
                        sb.AppendLine($"- {signal}");
                }
            }

            if (payload.RequiredActions != null && payload.RequiredActions.Count > 0)
            {
                sb.AppendLine("Required Actions:");
                foreach (var action in payload.RequiredActions)
                {
                    if (!string.IsNullOrWhiteSpace(action))
                        sb.AppendLine($"- {action}");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.Guidance))
                sb.AppendLine($"Guidance: {payload.Guidance}");
        }

        private static void AppendCodeFileSnapshot(StringBuilder sb, object value, string userGoal)
        {
            if (sb == null || value == null)
                return;

            if (value is not CodeFileSnapshotPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Code File Snapshot】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Code File Snapshot】");
            sb.AppendLine("以下是使用者附件中的文字/程式檔案快照。回答程式修改或 diff 任務時，只能根據此處內容與使用者要求推論。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);

            if (payload.Files == null || payload.Files.Count == 0)
                return;

            int remainingPromptChars = MaxCodeSnapshotPromptCharsTotal;
            foreach (var file in payload.Files.Take(4))
            {
                if (file == null)
                    continue;

                string content = file.Content ?? "";
                int promptLimit = Math.Min(MaxCodeSnapshotPromptCharsPerFile, remainingPromptChars);

                // Code Agent v1.5：針對性 / 分段擷取（檔頭 + 結構骨架 + 目標關鍵字上下文）。
                var focused = CodeContextExtractor.Build(content, userGoal, promptLimit);
                string promptContent = focused.Text;
                bool promptTruncated = focused.Chunked;
                string sourceOutline = BuildCodeSourceOutline(content, MaxCodeSourceOutlineCharsPerFile);
                remainingPromptChars = Math.Max(0, remainingPromptChars - promptContent.Length);

                sb.AppendLine();
                sb.AppendLine($"File: {file.FileName}");
                sb.AppendLine($"Path: {file.RelativePath}");
                sb.AppendLine($"Language: {file.Language}");
                sb.AppendLine($"Chars: {file.CharacterCount}; Lines: {file.LineCount}; Truncated: {file.IsTruncated}");
                sb.AppendLine($"PromptChars: {promptContent.Length}; PromptTruncated: {promptTruncated}");
                if (focused.Chunked)
                {
                    sb.AppendLine($"ChunkMap: included {focused.IncludedLines}/{focused.TotalLines} lines; segments=[{string.Join(", ", focused.SegmentRanges)}]");
                    sb.AppendLine("分段分析：以上摘錄是針對目標關鍵字與程式結構挑選的重點區段，非整份檔案。若需要某個未列出的區段，請指名行號範圍或函式名稱。");
                    sb.AppendLine("Do not claim a comprehensive whole-file fix when content is chunked. Prefer a narrow, evidence-backed patch or explain that targeted follow-up is required.");
                }

                if (!string.IsNullOrWhiteSpace(sourceOutline))
                {
                    sb.AppendLine("SourceOutline:");
                    sb.AppendLine("```");
                    sb.AppendLine(sourceOutline);
                    sb.AppendLine("```");
                }

                sb.AppendLine("PromptExcerpt:");
                sb.AppendLine("```");
                sb.AppendLine(promptContent);
                sb.AppendLine("```");

                if (remainingPromptChars <= 0)
                {
                    sb.AppendLine("Additional snapshot content omitted from prompt because the code prompt budget was reached.");
                    break;
                }
            }
        }

        private static string BuildCodeSourceOutline(string? content, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(content) || maxChars <= 0)
                return "";

            var outline = new List<string>();
            string[] lines = content.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);

            for (int index = 0; index < lines.Length && outline.Count < 120; index++)
            {
                string raw = lines[index] ?? "";
                string line = raw.Trim();

                if (line.Length == 0)
                    continue;

                bool interesting =
                    line.StartsWith("package ", StringComparison.Ordinal) ||
                    line.StartsWith("import ", StringComparison.Ordinal) ||
                    line.Contains(" class ", StringComparison.Ordinal) ||
                    line.StartsWith("class ", StringComparison.Ordinal) ||
                    IsLikelyMemberSignature(line) ||
                    line.Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("FIXME", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("bug", StringComparison.OrdinalIgnoreCase) ||
                    line.Contains("Exception", StringComparison.Ordinal);

                if (!interesting)
                    continue;

                outline.Add($"{index + 1}: {TrimForPrompt(line, 220)}");
            }

            return TrimForPrompt(string.Join(Environment.NewLine, outline), maxChars);
        }

        private static bool IsLikelyMemberSignature(string line)
        {
            if (!line.Contains("(", StringComparison.Ordinal) ||
                !line.Contains(")", StringComparison.Ordinal))
                return false;

            if (line.StartsWith("if ", StringComparison.Ordinal) ||
                line.StartsWith("if(", StringComparison.Ordinal) ||
                line.StartsWith("for ", StringComparison.Ordinal) ||
                line.StartsWith("for(", StringComparison.Ordinal) ||
                line.StartsWith("while ", StringComparison.Ordinal) ||
                line.StartsWith("while(", StringComparison.Ordinal) ||
                line.StartsWith("switch ", StringComparison.Ordinal) ||
                line.StartsWith("switch(", StringComparison.Ordinal) ||
                line.StartsWith("catch ", StringComparison.Ordinal) ||
                line.StartsWith("catch(", StringComparison.Ordinal))
                return false;

            return line.Contains("public ", StringComparison.Ordinal) ||
                   line.Contains("private ", StringComparison.Ordinal) ||
                   line.Contains("protected ", StringComparison.Ordinal) ||
                   line.Contains("static ", StringComparison.Ordinal) ||
                   line.Contains("@Override", StringComparison.Ordinal);
        }

        private static void AppendCodeDiffArtifact(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not CodeDiffArtifactPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Code Diff Artifact】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Code Diff Artifact】");
            sb.AppendLine("以下是 code-capability 建立的 diff artifact。若 Status=draft，代表尚未套用，不能宣稱已修改檔案。");

            if (!string.IsNullOrWhiteSpace(payload.Title))
                sb.AppendLine($"Title: {payload.Title}");

            if (!string.IsNullOrWhiteSpace(payload.Status))
                sb.AppendLine($"Status: {payload.Status}");

            if (!string.IsNullOrWhiteSpace(payload.BaseLabel))
                sb.AppendLine($"Base: {payload.BaseLabel}");

            if (!string.IsNullOrWhiteSpace(payload.TargetLabel))
                sb.AppendLine($"Target: {payload.TargetLabel}");

            if (payload.Files != null && payload.Files.Count > 0)
            {
                sb.AppendLine("Files:");
                foreach (var file in payload.Files.Take(12))
                {
                    if (file == null)
                        continue;

                    sb.AppendLine($"- {file.ChangeType}: {file.Path} (+{file.AddedLines}/-{file.RemovedLines})");

                    if (!string.IsNullOrWhiteSpace(file.Summary))
                        sb.AppendLine($"  Summary: {file.Summary}");
                }
            }

            if (payload.Notes != null && payload.Notes.Count > 0)
            {
                sb.AppendLine("Notes:");
                foreach (var note in payload.Notes.Take(8))
                {
                    if (!string.IsNullOrWhiteSpace(note))
                        sb.AppendLine($"- {note}");
                }
            }

            if (!string.IsNullOrWhiteSpace(payload.UnifiedDiff))
            {
                sb.AppendLine("UnifiedDiff:");
                sb.AppendLine("```diff");
                sb.AppendLine(payload.UnifiedDiff);
                sb.AppendLine("```");
            }
        }

        private static void AppendSearchSummary(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not SearchSummaryPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【Search Summary】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Search Summary】");
            sb.AppendLine("?? 以下資料為唯一可信來源，不可自行補充未提供資訊。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);
            else
                sb.AppendLine("（無摘要）");

            if (payload.Items == null || payload.Items.Count == 0)
                return;

            sb.AppendLine();
            sb.AppendLine("【Sources】");

            int index = 1;
            foreach (var item in payload.Items)
            {
                if (item == null)
                    continue;

                sb.AppendLine($"{index}. {item.Title}");

                if (!string.IsNullOrWhiteSpace(item.Source))
                    sb.AppendLine($"   Url: {item.Source}");

                if (!string.IsNullOrWhiteSpace(item.Date))
                    sb.AppendLine($"   Date: {item.Date}");

                index++;
            }

            if (index == 1)
                sb.AppendLine("（無來源）");
        }
        private readonly AgentParallelPlanner _parallelPlanner = new();
        private readonly AgentRuntimeProfileResolver _agentProfileResolver = new();
        private static void AppendFileSummary(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not FileSummaryPayload payload)
            {
                sb.AppendLine();
                sb.AppendLine("【File Summary】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【File Summary】");
            sb.AppendLine("?? 以下附件資訊為高優先來源，回答時應優先根據附件內容。");

            if (!string.IsNullOrWhiteSpace(payload.Summary))
                sb.AppendLine(payload.Summary);
            else
                sb.AppendLine("（無附件摘要）");

            if (payload.Items == null || payload.Items.Count == 0)
                return;

            sb.AppendLine();
            sb.AppendLine("【Attached Files】");

            int index = 1;
            foreach (var item in payload.Items)
            {
                if (item == null)
                    continue;

                sb.AppendLine($"{index}. {item.FileName}");

                if (!string.IsNullOrWhiteSpace(item.FileType))
                    sb.AppendLine($"   Type: {item.FileType}");

                if (!string.IsNullOrWhiteSpace(item.MimeType))
                    sb.AppendLine($"   Mime: {item.MimeType}");

                if (!string.IsNullOrWhiteSpace(item.ContentPreview))
                    sb.AppendLine($"   Hint: {item.ContentPreview}");

                index++;
            }

            if (index == 1)
                sb.AppendLine("（無附件）");
        }
        private static void AppendSearchResults(StringBuilder sb, object value)
        {
            if (sb == null || value == null)
                return;

            if (value is not System.Collections.IEnumerable enumerable)
            {
                sb.AppendLine();
                sb.AppendLine("【Search Results】");
                sb.AppendLine(value.ToString() ?? "");
                return;
            }

            sb.AppendLine();
            sb.AppendLine("【Search Results】");

            int index = 1;
            foreach (var item in enumerable)
            {
                if (item == null)
                    continue;

                string title = ReadObjectString(item, "Title");
                string url = ReadObjectString(item, "Url");
                string snippet = ReadObjectString(item, "Snippet");
                string date = ReadObjectString(item, "Date");

                sb.AppendLine($"{index}. {title}");

                if (!string.IsNullOrWhiteSpace(snippet))
                    sb.AppendLine($"   Snippet: {snippet}");

                if (!string.IsNullOrWhiteSpace(url))
                    sb.AppendLine($"   Url: {url}");

                if (!string.IsNullOrWhiteSpace(date))
                    sb.AppendLine($"   Date: {date}");

                index++;
            }

            if (index == 1)
                sb.AppendLine("（無結果）");
        }

        private static string ReadObjectString(object item, string propertyName)
        {
            if (item == null || string.IsNullOrWhiteSpace(propertyName))
                return "";

            var prop = item.GetType().GetProperty(
                propertyName,
                BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

            if (prop == null)
                return "";

            var value = prop.GetValue(item);
            return value?.ToString() ?? "";
        }
    }
}
