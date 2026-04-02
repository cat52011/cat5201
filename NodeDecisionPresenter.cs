using System;

namespace test
{
    public sealed class NodeDecisionPresenter
    {
        private readonly MainWindow _main;

        public NodeDecisionPresenter(MainWindow main)
        {
            _main = main;
        }

        public void Present(NodeExecutionDecision decision)
        {
            string requestedBaseModel = string.IsNullOrWhiteSpace(decision.RequestedModelId)
                ? decision.ModelId
                : decision.RequestedModelId;

            string actualBaseModel = string.IsNullOrWhiteSpace(decision.ActualModelId)
                ? decision.ModelId
                : decision.ActualModelId;

            string requestedModelLabel = GetRuntimeModelLabel(requestedBaseModel);
            string actualModelLabel = GetRuntimeModelLabel(actualBaseModel);

            string modelLabel =
                string.Equals(actualModelLabel, requestedModelLabel, StringComparison.OrdinalIgnoreCase)
                    ? actualModelLabel
                    : $"{actualModelLabel} ← {requestedModelLabel}";

            string taskLabel = NodeTaskModeHelper.ToDisplayName(decision.TaskMode);
            string confidenceText = $"{decision.Confidence:0.00}";

            string taskSummary = $"{taskLabel} / {confidenceText}";

            if (!string.IsNullOrWhiteSpace(decision.CapabilityReason))
                taskSummary += $" / {decision.CapabilityReason}";

            if (decision.RuntimeFallbackUsed && !string.IsNullOrWhiteSpace(decision.RuntimeFallbackSummary))
                taskSummary += $" / {decision.RuntimeFallbackSummary}";

            if (decision.RuntimeFallbackUsed)
            {
                _main.SetDecisionVisualization(
                    status: decision.StatusLabel,
                    mode: _main.IsAutoModelSelectionEnabled() ? "Auto" : "Manual",
                    resolver: decision.ResolverLabel + " + Runtime Fallback",
                    model: modelLabel,
                    taskSummary: taskSummary,
                    statusBrushHex: "#FFE9E9",
                    statusTextBrushHex: "#9B2C2C");
                return;
            }

            if (decision.CapabilityAdjusted)
            {
                _main.SetDecisionVisualization(
                    status: decision.StatusLabel,
                    mode: _main.IsAutoModelSelectionEnabled() ? "Auto" : "Manual",
                    resolver: decision.ResolverLabel,
                    model: modelLabel,
                    taskSummary: taskSummary,
                    statusBrushHex: "#FFF4E8",
                    statusTextBrushHex: "#9A5A00");
                return;
            }

            if (decision.UsedApiResolver)
            {
                if (decision.UsedFallbackToRules)
                {
                    _main.SetDecisionVisualization(
                        status: decision.StatusLabel,
                        mode: "Auto",
                        resolver: decision.ResolverLabel,
                        model: modelLabel,
                        taskSummary: taskSummary,
                        statusBrushHex: "#FFF4E8",
                        statusTextBrushHex: "#9A5A00");
                    return;
                }

                _main.SetDecisionVisualization(
                    status: decision.StatusLabel,
                    mode: "Auto",
                    resolver: decision.ResolverLabel,
                    model: modelLabel,
                    taskSummary: taskSummary,
                    statusBrushHex: "#EAF4FF",
                    statusTextBrushHex: "#245A9B");
                return;
            }

            if (_main.IsAutoModelSelectionEnabled())
            {
                _main.SetDecisionVisualization(
                    status: decision.StatusLabel,
                    mode: "Auto",
                    resolver: decision.ResolverLabel,
                    model: modelLabel,
                    taskSummary: taskSummary,
                    statusBrushHex: "#EEF7EA",
                    statusTextBrushHex: "#2E6A2E");
                return;
            }

            _main.SetDecisionVisualization(
                status: decision.StatusLabel,
                mode: "Manual",
                resolver: decision.ResolverLabel,
                model: modelLabel,
                taskSummary: taskSummary,
                statusBrushHex: "#EDEDED",
                statusTextBrushHex: "#404040");
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
    }
}