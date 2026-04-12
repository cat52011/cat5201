using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class NodeExecutionDecisionResolver
    {
        private readonly AgentRuntimeProfileResolver _agentProfileResolver = new();
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
            var agent = AgentRegistry.Get(selectedAgentId);

            string selectedModel = _main.GetNodeSelectedModel(node);
            var agentProfile = _agentProfileResolver.Resolve(
                agent,
                preferredModelId: selectedModel,
                preferredTaskMode: _main.GetNodeTaskMode(node));
            if (!_main.IsAutoModelSelectionEnabled())
            {
                var manualTask = ResolveAndPersistTaskMode(node, topText);

                var manualDecision = new NodeExecutionDecision
                {
                    RequestedAgentId = agent.Id,
                    ActualAgentId = agent.Id,

                    RequestedModelId = AiModelHelper.NormalizeNodeModel(agentProfile.RuntimeModelId),
                    ModelId = AiModelHelper.NormalizeNodeModel(agentProfile.RuntimeModelId),

                    TaskMode = manualTask.Mode,
                    ResolverLabel = "Manual",
                    StatusLabel = "Manual",
                    Confidence = 1.0,
                    ResolverReason = manualTask.Reason,
                    ResolverKeywords = manualTask.MatchedKeywords ?? Array.Empty<string>(),
                    UsedApiResolver = false,
                    UsedFallbackToRules = false,
                    UseStreaming = true
                };

                return ApplyCapabilityCheck(node, manualDecision);
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

                    var apiDecision = new NodeExecutionDecision
                    {
                        RequestedAgentId = agent.Id,
                        ActualAgentId = agent.Id,

                        RequestedModelId = resolvedModel,
                        ModelId = resolvedModel,

                        TaskMode = resolvedMode,
                        ResolverLabel = "Responses API",
                        StatusLabel = "API Auto",
                        Confidence = resolution.Confidence,
                        ResolverReason = "由 API resolver 根據輸入內容判定模型與任務模式",
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
                    string fallbackModel = _modelSelection.ResolveRuleAutoModel(
                        fallbackTask.Mode,
                        selectedModel);

                    var fallbackDecision = new NodeExecutionDecision
                    {
                        RequestedAgentId = agent.Id,
                        ActualAgentId = agent.Id,

                        RequestedModelId = AiModelHelper.NormalizeNodeModel(fallbackModel),
                        ModelId = AiModelHelper.NormalizeNodeModel(fallbackModel),

                        TaskMode = fallbackTask.Mode,
                        ResolverLabel = "Rules (fallback)",
                        StatusLabel = "API Auto",
                        Confidence = 0.30,
                        ResolverReason = fallbackTask.Reason,
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
                RequestedAgentId = agent.Id,
                ActualAgentId = agent.Id,

                RequestedModelId = AiModelHelper.NormalizeNodeModel(autoModel),
                ModelId = AiModelHelper.NormalizeNodeModel(autoModel),

                TaskMode = ruleTask.Mode,
                ResolverLabel = "Rules",
                StatusLabel = "Rule Auto",
                Confidence = ruleTask.Confidence,
                ResolverReason = ruleTask.Reason,
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