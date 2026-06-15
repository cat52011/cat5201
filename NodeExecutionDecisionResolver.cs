using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class NodeExecutionDecisionResolver
    {
        private readonly AgentRuntimeProfileResolver _agentProfileResolver = new();
        private readonly AgentSelectionResolver _agentSelectionResolver = new();

        private readonly AiServiceRouter _router;
        private readonly MainWindow _main;
        private readonly AiAutoModelResolverService _autoResolver;
        private readonly NodeModelSelectionService _modelSelection;

        public NodeExecutionDecisionResolver(
            AiServiceRouter router,
            MainWindow main,
            AiAutoModelResolverService autoResolver,
            NodeModelSelectionService modelSelection)
        {
            _router = router;
            _main = main;
            _autoResolver = autoResolver;
            _modelSelection = modelSelection;
        }

        public async Task<NodeExecutionDecision> ResolveAsync(
            NodeControl node,
            string topText,
            CancellationToken ct)
        {
            string selectedAgentId = _main.GetNodeSelectedAgent(node);
            var selectedAgent = AgentRegistry.Get(selectedAgentId);

            string selectedModel = _main.GetNodeSelectedModel(node);
            var attachments = _main.GetAttachmentsForNode(node);

            // ===== Manual：保留原本手動 agent =====
            if (!_main.IsAutoModelSelectionEnabled())
            {
                var manualTaskMode = NodeTaskModeHelper.Normalize(_main.GetNodeTaskMode(node));

                var manualProfile = _agentProfileResolver.Resolve(
                    selectedAgent,
                    preferredModelId: selectedModel,
                    preferredTaskMode: manualTaskMode);

                var manualDecision = new NodeExecutionDecision
                {
                    RequestedAgentId = selectedAgent.Id,
                    ActualAgentId = selectedAgent.Id,

                    RequestedModelId = AiModelHelper.NormalizeNodeModel(manualProfile.RuntimeModelId),
                    ModelId = AiModelHelper.NormalizeNodeModel(manualProfile.RuntimeModelId),

                    TaskMode = manualTaskMode,
                    ResolverLabel = "Manual",
                    StatusLabel = "Manual",
                    Confidence = 1.0,
                    ResolverReason = "Manual mode: selected agent/model/task are preserved.",
                    ResolverKeywords = Array.Empty<string>(),
                    UsedApiResolver = false,
                    UsedFallbackToRules = false,
                    UseStreaming = true
                };

                return ApplyCapabilityCheck(node, manualDecision);
            }

            // ===== Auto：先解 task，再自動選 agent =====
            if (_main.IsAdvancedAutoResolverEnabled())
            {
                try
                {
                    var apiResolution = await _autoResolver.ResolveAsync(
                        topText,
                        attachments,
                        ct);

                    var resolvedMode = NodeTaskModeHelper.Normalize(apiResolution.TaskMode);
                    _main.SetNodeTaskMode(node, resolvedMode);

                    var autoAgentSelection = _agentSelectionResolver.Resolve(
                        topText,
                        resolvedMode,
                        attachments,
                        selectedAgentId);

                    var actualAgent = AgentRegistry.Get(autoAgentSelection.AgentId);

                    // 使用者自訂路由優先：若該任務模式已釘選模型，覆蓋 API resolver 的建議。
                    bool pinned = _modelSelection.TryGetOverride(resolvedMode, out var pinnedModel);
                    string resolvedModel = pinned
                        ? pinnedModel
                        : AiModelHelper.NormalizeNodeModel(apiResolution.RecommendedModel);

                    var apiDecision = new NodeExecutionDecision
                    {
                        RequestedAgentId = selectedAgent.Id,
                        ActualAgentId = actualAgent.Id,

                        RequestedModelId = resolvedModel,
                        ModelId = resolvedModel,

                        TaskMode = resolvedMode,
                        ResolverLabel = "Responses API + Agent Rules",
                        StatusLabel = "API Auto",
                        Confidence = Math.Max(apiResolution.Confidence, autoAgentSelection.Confidence),
                        ResolverReason =
                            $"Agent: {autoAgentSelection.Reason} / " +
                            (pinned
                                ? $"Task/Model: 任務模式 {resolvedMode} 由使用者自訂路由指定模型（覆蓋 API 建議）"
                                : $"Task/Model: 由 API resolver 根據輸入內容判定模型與任務模式"),
                        ResolverKeywords = Array.Empty<string>(),
                        UsedApiResolver = true,
                        UsedFallbackToRules = false,
                        UseStreaming = true
                    };

                    return ApplyCapabilityCheck(node, apiDecision);
                }
                catch
                {
                    var fallbackTask = ResolveAndPersistTaskMode(node, topText);

                    var autoAgentSelection = _agentSelectionResolver.Resolve(
                        topText,
                        fallbackTask.Mode,
                        attachments,
                        selectedAgentId);

                    var actualAgent = AgentRegistry.Get(autoAgentSelection.AgentId);

                    string fallbackModel = _modelSelection.ResolveRuleAutoModel(
                        fallbackTask.Mode,
                        selectedModel);

                    var fallbackDecision = new NodeExecutionDecision
                    {
                        RequestedAgentId = selectedAgent.Id,
                        ActualAgentId = actualAgent.Id,

                        RequestedModelId = AiModelHelper.NormalizeNodeModel(fallbackModel),
                        ModelId = AiModelHelper.NormalizeNodeModel(fallbackModel),

                        TaskMode = fallbackTask.Mode,
                        ResolverLabel = "Rules (fallback) + Agent Rules",
                        StatusLabel = "API Auto",
                        Confidence = Math.Max(0.30, autoAgentSelection.Confidence),
                        ResolverReason =
                            $"Agent: {autoAgentSelection.Reason} / " +
                            $"Task/Model fallback: {fallbackTask.Reason}",
                        ResolverKeywords = fallbackTask.MatchedKeywords ?? Array.Empty<string>(),
                        UsedApiResolver = true,
                        UsedFallbackToRules = true,
                        UseStreaming = true
                    };

                    return ApplyCapabilityCheck(node, fallbackDecision);
                }
            }

            var ruleTask = ResolveAndPersistTaskMode(node, topText);

            string autoModel = _modelSelection.ResolveRuleAutoModel(
                ruleTask.Mode,
                selectedModel);

            var ruleDecision = new NodeExecutionDecision
            {
                RequestedAgentId = selectedAgent.Id,
                ActualAgentId = selectedAgent.Id,

                RequestedModelId = AiModelHelper.NormalizeNodeModel(autoModel),
                ModelId = AiModelHelper.NormalizeNodeModel(autoModel),

                TaskMode = ruleTask.Mode,
                ResolverLabel = "Rule Auto Model",
                StatusLabel = "Rule Auto",
                Confidence = ruleTask.Confidence,
                ResolverReason = $"Task/Model: {ruleTask.Reason}. Agent is not changed in Rule Auto mode.",
                ResolverKeywords = ruleTask.MatchedKeywords ?? Array.Empty<string>(),
                UsedApiResolver = false,
                UsedFallbackToRules = false,
                UseStreaming = true
            };

            return ApplyCapabilityCheck(node, ruleDecision);
        }

        private NodeTaskModeResolution ResolveAndPersistTaskMode(
            NodeControl node,
            string topText)
        {
            var resolution = NodeTaskModeResolver.Resolve(topText);
            var normalized = NodeTaskModeHelper.Normalize(resolution.Mode);

            _main.SetNodeTaskMode(node, normalized);

            return new NodeTaskModeResolution
            {
                Mode = normalized,
                Reason = resolution.Reason,
                Confidence = resolution.Confidence,
                MatchedKeywords = resolution.MatchedKeywords
            };
        }

        private NodeExecutionDecision ApplyCapabilityCheck(
            NodeControl node,
            NodeExecutionDecision decision)
        {
            var attachments = _main.GetAttachmentsForNode(node);

            bool hasImageAttachments = attachments.Any(a =>
                string.Equals(a.Kind, "image", StringComparison.OrdinalIgnoreCase));

            bool hasFileAttachments = attachments.Any(a =>
                !string.Equals(a.Kind, "image", StringComparison.OrdinalIgnoreCase));

            bool requireSearchCapability =
                _main.IsAutoModelSelectionEnabled() &&
                decision.TaskMode == NodeTaskMode.Research;

            var check = AiCapabilityGuard.Resolve(
                requestedModelId: decision.ModelId,
                taskMode: decision.TaskMode,
                wantsStreaming: true,
                hasImageAttachments: hasImageAttachments,
                hasFileAttachments: hasFileAttachments,
                requireSearchCapability: requireSearchCapability);

            decision.RequestedModelId = check.RequestedModelId;
            decision.ModelId = check.ResolvedModelId;
            decision.UseStreaming = check.StreamingAllowed;

            decision.CapabilityAdjusted = check.ModelAdjusted || check.StreamingAdjusted;
            decision.CapabilityReason = check.Reason ?? "";

            decision.CapabilityRequestedModelId = check.RequestedModelId;
            decision.CapabilityResolvedModelId = check.ResolvedModelId;
            decision.CapabilityRequired = check.RequiredCapabilities;
            decision.CapabilityMissing = check.MissingCapabilities;
            decision.CapabilityStreamingAdjusted = check.StreamingAdjusted;

            if (decision.CapabilityAdjusted)
            {
                decision.ResolverLabel = string.IsNullOrWhiteSpace(decision.ResolverLabel)
                    ? "Capability Guard"
                    : decision.ResolverLabel + " + Capability Guard";
            }

            return decision;
        }
    }
}
