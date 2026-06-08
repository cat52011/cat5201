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

                decision.RequestedAgentId = forcedAgent.Id;
                decision.ActualAgentId = forcedAgent.Id;

                decision.RequestedModelId = AiModelHelper.NormalizeNodeModel(profile.RuntimeModelId);
                decision.ModelId = AiModelHelper.NormalizeNodeModel(profile.RuntimeModelId);
                decision.ActualModelId = "";
                decision.ForceSingleModel = true;
                decision.ResolverLabel += " + Forced Agent Profile";
                decision.ResolverReason =
                    $"Delegated agent forced profile: {forcedAgent.Id} / model: {profile.RuntimeModelId}";
            }

            var runtimeAgent = AgentRegistry.Get(decision.ActualAgentId);
            bool allowAgentFirstAutomation =
                _main.IsAutoModelSelectionEnabled() &&
                _main.IsAdvancedAutoResolverEnabled();

            _main.SetLiveDecisionResolving(request.Node, decision);
            // 2. capability layer
            string capabilityAugmentedText = topText;

            var capabilityContext = new AgentExecutionContext
            {
                Node = request.Node,
                Agent = runtimeAgent,
                TopText = topText,
                TaskMode = decision.TaskMode,
                Attachments = _main.GetAttachmentsForNode(request.Node)
            };

            var capabilityPlan = AgentCapabilityPlanner.Build(
    runtimeAgent,
    topText,
    decision.TaskMode,
    capabilityContext.Attachments != null && capabilityContext.Attachments.Count > 0);

            System.Diagnostics.Debug.WriteLine(
                $"[CapabilityPlan] Agent={runtimeAgent.Id} Required={string.Join(", ", capabilityPlan.RequiredCapabilityIds)} Order={string.Join(" -> ", capabilityPlan.OrderedCapabilityIds)} Reason={capabilityPlan.Reason}");

            var orderedCapabilities = capabilityPlan.OrderedCapabilityIds
                .Select(id => AgentCapabilityRegistry.All.FirstOrDefault(c =>
                    string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase)))
                .Where(c => c != null)
                .Cast<IAgentCapability>()
                .ToList();
            bool runCapabilityLayer =
                allowAgentFirstAutomation &&
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

                    // ⭐ 關鍵：Required capability 強制允許
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
                            Attachments = _main.GetAttachmentsForNode(request.Node)
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

            if (allowAgentFirstAutomation && request.DelegationDepth == 0)
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

            // 🔥 加這行
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
                        enforceSynthesisFormat: true);
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

            bool enforceFinalSynthesisFormat =
                hasVerifiedFacts ||
                !string.IsNullOrWhiteSpace(workspaceBlock);

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

                parts.Add(BuildFinalOutputFormatBlock());
                parts.Add("【Delegated Analysis Omitted】\nDelegated/parallel agent text was omitted because structured verified_facts exist. Use delegated analysis only as internal reasoning, not as a source for numeric facts.");
                parts.Add("【目前任務】\n" + capabilityAugmentedText);

                finalInput = string.Join("\n\n", parts);
            }

            // 5. execution
            AiFallbackExecutionResult execution;

            if (synthesisExecution != null && synthesisExecution.IsSuccess)
            {
                execution = FinalAnswerSanitizer.Sanitize(
                    synthesisExecution,
                    enforceSynthesisFormat: true);

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
                    request.OnDelta,
                    request.UseStreaming,
                    request.CancellationToken);

                execution = FinalAnswerSanitizer.Sanitize(
                    execution,
                    enforceSynthesisFormat: enforceFinalSynthesisFormat);
            }

            // 6. finalize
            var workspaceSummary = workspace.BuildSummary();
            decision = _executionFinalizer.FinalizeDecision(decision, execution);
            decision.ActualAgentId = runtimeAgent.Id;
            decision.CapabilityTrace = capabilityTrace;
            decision.DelegationTrace = delegationTrace;
            decision.WorkspaceSummary = workspaceSummary?.SummaryText ?? "";
            decision.WorkspaceArtifactDetails = workspaceSummary?.ArtifactDetails ?? Array.Empty<string>();


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

            string synthesisInput =
        $@"你是 final synthesizer。你的任務是把 shared workspace 整理成使用者真正需要看的最終答案。

【使用者原始任務】
{originalInput}

【Shared Workspace】
{workspaceBlock}

【最高優先規則】
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
- 最終答案長度控制在一般回答可讀範圍內。

請現在輸出最終答案：";

            return await _executeWithFallbackAsync(
                node,
                synthesisInput,
                synthesisDecision,
                null,
                false,
                ct);
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
            sb.AppendLine("⚠️ 以下資料為唯一可信來源，不可自行補充未提供資訊。");

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
            sb.AppendLine("⚠️ 以下附件資訊為高優先來源，回答時應優先根據附件內容。");

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
