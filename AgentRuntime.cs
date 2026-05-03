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

            var orderedCapabilities = AgentCapabilityRegistry.All
                .Where(c => c != null)
                .OrderByDescending(c => runtimeAgent.IsPreferredCapability(c.Id))
                .ThenBy(c => c.Id, StringComparer.OrdinalIgnoreCase)
                .ToList();

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

                if (!runtimeAgent.IsCapabilityAllowed(capability.Id))
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
                        Summary = "not allowed by agent policy"
                    });
                    continue;
                }

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

                if (capabilityResult.Data != null && capabilityResult.Data.Count > 0)
                {
                    foreach (var kv in capabilityResult.Data)
                    {
                        capabilityData[kv.Key] = kv.Value;
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
            string capabilityDataBlock = BuildCapabilityDataBlock(capabilityData);

            string finalInput = capabilityAugmentedText;

            if (!string.IsNullOrWhiteSpace(capabilityDataBlock))
            {
                finalInput =
                    capabilityDataBlock +
                    "\n\n【目前任務】\n" +
                    capabilityAugmentedText;
            }

            if (!string.IsNullOrWhiteSpace(delegatedContext))
            {
                finalInput =
                    (!string.IsNullOrWhiteSpace(capabilityDataBlock)
                        ? capabilityDataBlock + "\n\n"
                        : "") +
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

        private static string BuildCapabilityDataBlock(
            IReadOnlyDictionary<string, object> capabilityData)
        {
            if (capabilityData == null || capabilityData.Count == 0)
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("【Capability Data】");
            sb.AppendLine("以下內容來自系統工具/能力層的真實輸出，屬高優先參考。");
            sb.AppendLine("回答時請優先使用這些資料，不要忽略，也不要憑空改寫來源。");

            foreach (var kv in capabilityData)
            {
                if (string.IsNullOrWhiteSpace(kv.Key) || kv.Value == null)
                    continue;

                if (string.Equals(kv.Key, "search_summary", StringComparison.OrdinalIgnoreCase))
                {
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