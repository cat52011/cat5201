using System;
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
            var delegationTrace = new System.Collections.Generic.List<AgentDelegationTraceItem>();

            // 1. 先解出 execution decision
            var decision = await _decisionResolver.ResolveAsync(
                request.Node,
                topText,
                request.CancellationToken);

            decision.RequestedAgentId = request.Agent.Id;
            if (string.IsNullOrWhiteSpace(decision.ActualAgentId))
                decision.ActualAgentId = request.Agent.Id;

            _main.SetLiveDecisionResolving(request.Node, decision);

            // 2. 規劃 delegation（加深度保護）
            IReadOnlyList<AgentDelegationRequest> plans;

            if (request.DelegationDepth >= 2)
            {
                plans = Array.Empty<AgentDelegationRequest>();
            }
            else
            {
                plans = _delegationPlanner.Plan(
                    request.Agent,
                    topText,
                    decision.TaskMode);
            }

            // 3. 執行 delegation
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
                        FromAgentId = request.Agent.Id,
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
                        FromAgentId = request.Agent.Id,
                        ToAgentId = subAgent.Id,
                        Instruction = plan.Instruction,
                        OutputSummary = "",
                        Success = false,
                        ErrorMessage = ex.Message
                    });
                }
            }

            // 4. 把 delegation 結果併入主輸入
            string finalInput = topText;

            if (!string.IsNullOrWhiteSpace(delegatedContext))
            {
                finalInput =
                    "以下是其他代理提供的補充資訊：\n" +
                    delegatedContext +
                    "\n請基於以上資訊完成目前任務：\n" +
                    topText;
            }

            // 5. 主 execution
            var execution = await _executeWithFallbackAsync(
                request.Node,
                finalInput,
                decision,
                request.OnDelta,
                request.UseStreaming,
                request.CancellationToken);

            // 6. finalize
            decision = _executionFinalizer.FinalizeDecision(decision, execution);

            if (string.IsNullOrWhiteSpace(decision.ActualAgentId))
                decision.ActualAgentId = request.Agent.Id;
            decision.DelegationTrace = delegationTrace;

            return new AgentExecutionResult
            {
                Decision = decision,
                Execution = execution,
                DelegationTrace = delegationTrace
            };
        }
    }
}