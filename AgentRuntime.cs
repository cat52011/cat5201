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

            if (request.Node == null)
                throw new InvalidOperationException("AgentExecutionRequest.Node 不可為 null。");

            if (request.Agent == null)
                throw new InvalidOperationException("AgentExecutionRequest.Agent 不可為 null。");

            string topText = request.TopText ?? "";
            if (request.Workspace == null)
            {
                throw new InvalidOperationException(
                    "AgentExecutionRequest.Workspace 不可為 null。Parallel / Delegate flow 必須共享同一個 workspace instance。");
            }

            var workspace = request.Workspace;
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
                request.Node,
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

                string forcedRuntimeModel = _main.IsAutoModelSelectionEnabled()
                    ? AiAutoCostPolicy.NormalizeForAuto(profile.RuntimeModelId)
                    : AiModelHelper.NormalizeNodeModel(profile.RuntimeModelId);

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

            _main.SetLiveDecisionResolving(request.Node, decision);
            // 2. capability layer
            string capabilityAugmentedText = topText;

            var capabilityContext = new AgentExecutionContext
            {
                Node = request.Node,
                Agent = runtimeAgent,
                TopText = topText,
                TaskMode = decision.TaskMode,
                Attachments = _main.GetAttachmentsForNode(request.Node),
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

            workspace.Add(
                AgentWorkspaceBuilder.FromCapabilityData(
                    workspace,
                    request.Node,
                    runtimeAgent.Id,
                    "orchestration_plan",
                    orchestrationPlan));

            System.Diagnostics.Debug.WriteLine(
                $"[CapabilityPlan] Agent={runtimeAgent.Id} Required={string.Join(", ", capabilityPlan.RequiredCapabilityIds)} Order={string.Join(" -> ", capabilityPlan.OrderedCapabilityIds)} Reason={capabilityPlan.Reason}");

            var orderedCapabilities = capabilityPlan.OrderedCapabilityIds
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
                        continue;
                    }
                    bool isRequired = capabilityPlan.IsRequired(capability.Id);

                    if (!runtimeAgent.IsCapabilityAllowed(capability.Id) && !isRequired)
                    {
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
                        continue;
                    }

                    if (isRequired)
                        canHandle = true;

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

                        if (capabilityPlan.IsRequired(capability.Id))
                        {
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
                                    request.Node,
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

                        capabilityAugmentedText = capabilityResult.AugmentedPrompt;

                        capabilityContext = new AgentExecutionContext
                        {
                            Node = request.Node,
                            Agent = runtimeAgent,
                            TopText = capabilityAugmentedText,
                            TaskMode = decision.TaskMode,
                            Attachments = _main.GetAttachmentsForNode(request.Node),
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
                    }
                }
            }
            if (runCapabilityLayer &&
                capabilityPlan.RequiresFreshFacts &&
    !capabilityData.ContainsKey("verified_facts") &&
    !capabilityData.ContainsKey("search_summary"))
            {
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
                            Node = request.Node,
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
                                request.Node,
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
                        Node = request.Node,
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
        request.Node,
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
        request.Node,
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
                    request.Node,
                    runtimeAgent,
                    capabilityAugmentedText,
                    workspace,
                    decision,
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
                            request.Node,
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

            bool isFinanceTask = FinanceTaskDetector.IsFinanceLike(capabilityAugmentedText);
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

                if (hasCodeDiffDraft)
                    parts.Add(BuildCodeDiffOutputInstructionBlock());

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

                if (hasCodeDiffDraft)
                    parts.Add(BuildCodeDiffOutputInstructionBlock());

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
                if (hasCodeDiffDraft)
                    parts.Add(BuildCodeDiffOutputInstructionBlock());
                parts.Add("【Delegated Analysis Omitted】\nDelegated/parallel agent text was omitted because structured verified_facts exist. Use delegated analysis only as internal reasoning, not as a source for numeric facts.");
                parts.Add("【目前任務】\n" + capabilityAugmentedText);

                finalInput = string.Join("\n\n", parts);
            }

            // 5. execution
            AiFallbackExecutionResult execution;
            bool useStreamingForFinalExecution = request.UseStreaming && !hasCodeDiffDraft;

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
                    request.Node,
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
                            request.Node,
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
                            request.Node,
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
                        request.Node,
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
                                    request.Node,
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
                                    request.Node,
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
        private async Task<AiFallbackExecutionResult> RunFinalSynthesisAsync(
            NodeControl node,
            AgentDefinition rootAgent,
            string originalInput,
            AgentWorkspace workspace,
            NodeExecutionDecision rootDecision,
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
            bool isFinanceTask = FinanceTaskDetector.IsFinanceLike(originalInput);
            string synthesisInstructions = hasVerifiedFacts && isFinanceTask
                ? BuildFinanceFinalSynthesisInstructions()
                : BuildGeneralFinalSynthesisInstructions();

            string synthesisInput =
        $@"你是 final synthesizer。你的任務是把 shared workspace 整理成使用者真正需要看的最終答案。

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
            string resolved = AiAutoCostPolicy.NormalizeForAuto(requested);

            if (string.Equals(requested, resolved, StringComparison.OrdinalIgnoreCase))
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
13. 使用繁體中文。

【輸出格式】
請嚴格使用以下格式：

結論
- 用 2～4 點直接回答。
- 先說 TSM 與 MU 各自短期判斷。
- 若兩者相比，直接說哪個較穩、哪個彈性較大、哪個風險較高。

關鍵資料
- TSM：只列最重要的股價、財報或市場資料。最多 5 點。
- MU：只列最重要的股價、財報或市場資料。最多 5 點。
- 如果資料來源衝突，不要在這裡展開；只簡短標示「報價來源有衝突，詳見資料衝突」。

短期走勢判斷
- 分開寫 TSM 與 MU。
- 每檔最多 1 段。
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
6. 使用繁體中文。

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
                    AppendCodeFileSnapshot(sb, kv.Value);
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

        private static void AppendCodeFileSnapshot(StringBuilder sb, object value)
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
                string promptContent = promptLimit > 0
                    ? TrimForPrompt(content, promptLimit)
                    : "";
                bool promptTruncated = promptContent.Length < content.Trim().Length;
                string sourceOutline = BuildCodeSourceOutline(content, MaxCodeSourceOutlineCharsPerFile);
                remainingPromptChars = Math.Max(0, remainingPromptChars - promptContent.Length);

                sb.AppendLine();
                sb.AppendLine($"File: {file.FileName}");
                sb.AppendLine($"Path: {file.RelativePath}");
                sb.AppendLine($"Language: {file.Language}");
                sb.AppendLine($"Chars: {file.CharacterCount}; Lines: {file.LineCount}; Truncated: {file.IsTruncated}");
                sb.AppendLine($"PromptChars: {promptContent.Length}; PromptTruncated: {promptTruncated}");
                if (promptTruncated)
                {
                    sb.AppendLine("Note: Full snapshot is kept in workspace for validation, but only a compact outline and excerpt are sent to the model to control token cost.");
                    sb.AppendLine("Do not claim a comprehensive whole-file fix when PromptTruncated=True. Prefer a narrow, evidence-backed patch or explain that targeted follow-up is required.");
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
