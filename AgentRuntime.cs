using System;
using System.Collections.Generic;
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

            // 1. resolve decision
            var decision = await _decisionResolver.ResolveAsync(
                request.Node,
                topText,
                request.CancellationToken);

            if (string.IsNullOrWhiteSpace(decision.RequestedAgentId))
                decision.RequestedAgentId = request.Agent.Id;

            if (string.IsNullOrWhiteSpace(decision.ActualAgentId))
                decision.ActualAgentId = request.Agent.Id;

            var runtimeAgent = AgentRegistry.Get(decision.ActualAgentId);

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

            foreach (var capability in AgentCapabilityRegistry.All)
            {
                if (capability == null)
                    continue;

                if (capability.RequiredAgentCapability != AgentCapability.None &&
                    !runtimeAgent.Capabilities.HasFlag(capability.RequiredAgentCapability))
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

                if (!canHandle)
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

                AgentCapabilityResult capabilityResult;
                try
                {
                    capabilityResult = await capability.ExecuteAsync(
                        capabilityContext,
                        request.CancellationToken);
                }
                catch (Exception ex)
                {
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
                    continue;
                }

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

                if (capabilityResult.Handled &&
                    !string.IsNullOrWhiteSpace(capabilityResult.Output))
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

            // 3. delegation
            IReadOnlyList<AgentDelegationRequest> plans;

            if (request.DelegationDepth >= 2)
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
                }
            }

            // 4. merge
            string finalInput = capabilityAugmentedText;

            if (!string.IsNullOrWhiteSpace(delegatedContext))
            {
                finalInput =
                    "以下是其他代理提供的補充資訊：\n" +
                    delegatedContext +
                    "\n請基於以上資訊完成目前任務：\n" +
                    capabilityAugmentedText;
            }

            // 5. execution
            var execution = await _executeWithFallbackAsync(
                request.Node,
                finalInput,
                decision,
                request.OnDelta,
                request.UseStreaming,
                request.CancellationToken);

            // 6. finalize
            decision = _executionFinalizer.FinalizeDecision(decision, execution);
            decision.ActualAgentId = runtimeAgent.Id;
            decision.CapabilityTrace = capabilityTrace;
            decision.DelegationTrace = delegationTrace;

            return new AgentExecutionResult
            {
                Decision = decision,
                Execution = execution,
                CapabilityTrace = capabilityTrace,
                DelegationTrace = delegationTrace
            };
        }
    }
}