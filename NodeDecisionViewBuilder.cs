using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class NodeDecisionViewBuilder
    {
        public static NodeDecisionViewData BuildFromLog(AiExecutionLogEntry log)
        {
            if (log == null)
                return CreateEmpty();

            string requestedLabel = GetModelLabel(log.RequestedModelId);
            string plannedLabel = GetModelLabel(log.PlannedModelId);
            string actualLabel = GetModelLabel(log.ActualModelId);

            // 若有 capability guard 調整，主顯示優先反映 capability 轉移
            string capabilityRequestedLabel = GetModelLabel(log.CapabilityRequestedModelId);
            string capabilityResolvedLabel = GetModelLabel(log.CapabilityResolvedModelId);

            string displayFromLabel = requestedLabel;
            string displayToLabel = actualLabel;
            string agent = string.IsNullOrWhiteSpace(log.ActualAgentId)
    ? (string.IsNullOrWhiteSpace(log.RequestedAgentId) ? "-" : log.RequestedAgentId)
    : log.ActualAgentId;

            if (log.CapabilityAdjusted &&
                !string.IsNullOrWhiteSpace(log.CapabilityRequestedModelId) &&
                !string.IsNullOrWhiteSpace(log.CapabilityResolvedModelId))
            {
                displayFromLabel = capabilityRequestedLabel;
                displayToLabel = capabilityResolvedLabel;
            }

            string modelLabel =
                string.Equals(displayFromLabel, displayToLabel, StringComparison.OrdinalIgnoreCase)
                    ? displayToLabel
                    : $"{displayToLabel} ← {displayFromLabel}";

            string status = log.SelectionMode switch
            {
                "API Auto" => "API Auto",
                "Auto" => "Rule Auto",
                _ => "Manual"
            };

            string mode = log.SelectionMode switch
            {
                "Manual" => "Manual",
                _ => "Auto"
            };

            string resolver = string.IsNullOrWhiteSpace(log.Resolver)
                ? "-"
                : log.Resolver;

            string taskSummary = BuildTaskSummary(log);
            string reason = string.IsNullOrWhiteSpace(log.ResolverReason)
                ? "-"
                : log.ResolverReason;

            string keywords = BuildKeywordSummary(log);
            if (string.IsNullOrWhiteSpace(keywords))
                keywords = "-";

            string extra = BuildExtraSummary(log);
            if (string.IsNullOrWhiteSpace(extra))
                extra = "-";

            bool apiFallbackUsed =
                log.Resolver?.Contains("fallback", StringComparison.OrdinalIgnoreCase) == true;

            return new NodeDecisionViewData
            {
                Status = status,
                Mode = mode,
                Resolver = resolver,
                Model = modelLabel,
                TaskSummary = taskSummary,
                Reason = reason,
                Keywords = keywords,
                Extra = extra,
                Agent = agent,
                CapabilityAdjusted = log.CapabilityAdjusted,
                RuntimeFallbackUsed = log.RuntimeFallbackUsed,
                ApiFallbackUsed = apiFallbackUsed,
                Steps = BuildSteps(
                    log,
                    requestedLabel,
                    plannedLabel,
                    actualLabel,
                    resolver,
                    apiFallbackUsed)
            };
        }

        private static NodeDecisionViewData CreateEmpty()
        {
            return new NodeDecisionViewData
            {
                Status = "Manual",
                Mode = "Manual",
                Resolver = "-",
                Model = "-",
                TaskSummary = "-",
                Reason = "-",
                Keywords = "-",
                Extra = "-",
                Agent = "-",
                Steps = Array.Empty<NodeDecisionStepViewData>()
            };
        }

        private static IReadOnlyList<NodeDecisionStepViewData> BuildSteps(
            AiExecutionLogEntry log,
            string requestedLabel,
            string plannedLabel,
            string actualLabel,
            string resolver,
            bool apiFallbackUsed)
        {
            var steps = new List<NodeDecisionStepViewData>();

            steps.Add(BuildTaskModeStep(log));
            steps.Add(BuildModelSelectionStep(log, requestedLabel, plannedLabel, actualLabel));
            steps.Add(BuildResolverStep(log, resolver, apiFallbackUsed));
            steps.Add(BuildCapabilityStep(log));
            steps.Add(BuildFallbackStep(log, apiFallbackUsed));
            steps.Add(BuildExecutionStep(log));

            return steps;
        }

        private static NodeDecisionStepViewData BuildTaskModeStep(AiExecutionLogEntry log)
        {
            var lines = new List<string>
            {
                $"Task Mode: {Safe(log.TaskMode)}",
                $"Confidence: {log.Confidence:0.00}"
            };

            if (!string.IsNullOrWhiteSpace(log.ResolverReason))
                lines.Add($"Reason: {log.ResolverReason}");

            if (log.ResolverKeywords != null && log.ResolverKeywords.Count > 0)
            {
                var keywords = log.ResolverKeywords
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .Distinct(StringComparer.OrdinalIgnoreCase);

                lines.Add("Keywords: " + string.Join(", ", keywords));
            }

            return new NodeDecisionStepViewData
            {
                Title = "Task Mode",
                Detail = $"{Safe(log.TaskMode)} / confidence {log.Confidence:0.00}",
                State = NodeDecisionStepState.Info,
                Highlight = true,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildModelSelectionStep(
            AiExecutionLogEntry log,
            string requestedLabel,
            string plannedLabel,
            string actualLabel)
        {
            string summary;

            if (string.Equals(requestedLabel, actualLabel, StringComparison.OrdinalIgnoreCase))
                summary = actualLabel;
            else
                summary = $"{requestedLabel} → {actualLabel}";

            var lines = new List<string>
{
    $"Requested Model: {requestedLabel}",
    $"Planned Model: {plannedLabel}",
    $"Actual Model: {actualLabel}"
};

            if (log.CapabilityAdjusted &&
                !string.IsNullOrWhiteSpace(log.CapabilityRequestedModelId) &&
                !string.IsNullOrWhiteSpace(log.CapabilityResolvedModelId))
            {
                lines.Add($"Capability Redirect: {GetModelLabel(log.CapabilityRequestedModelId)} → {GetModelLabel(log.CapabilityResolvedModelId)}");
            }

            var state =
                string.Equals(requestedLabel, plannedLabel, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(plannedLabel, actualLabel, StringComparison.OrdinalIgnoreCase)
                    ? NodeDecisionStepState.Success
                    : NodeDecisionStepState.Warning;

            return new NodeDecisionStepViewData
            {
                Title = "Model Selection",
                Detail = summary,
                State = state,
                Highlight = true,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildResolverStep(
            AiExecutionLogEntry log,
            string resolver,
            bool apiFallbackUsed)
        {
            var lines = new List<string>
            {
                $"Resolver: {Safe(resolver)}",
                $"Selection Mode: {Safe(log.SelectionMode)}"
            };

            if (!string.IsNullOrWhiteSpace(log.ResolverReason))
                lines.Add($"Resolver Reason: {log.ResolverReason}");

            if (apiFallbackUsed)
                lines.Add("API resolver failed or downgraded, fallback to rules.");

            return new NodeDecisionStepViewData
            {
                Title = "Resolver",
                Detail = Safe(resolver),
                State = apiFallbackUsed ? NodeDecisionStepState.Warning : NodeDecisionStepState.Info,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildCapabilityStep(AiExecutionLogEntry log)
        {
            string detail;

            if (log.CapabilityAdjusted)
            {
                detail = $"{GetModelLabel(log.CapabilityRequestedModelId)} → {GetModelLabel(log.CapabilityResolvedModelId)}";
            }
            else
            {
                detail = "OK";
            }

            var lines = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequestedModelId))
                lines.Add($"Requested: {GetModelLabel(log.CapabilityRequestedModelId)}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityResolvedModelId))
                lines.Add($"Resolved: {GetModelLabel(log.CapabilityResolvedModelId)}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequired))
                lines.Add($"Required: {log.CapabilityRequired}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityMissing))
                lines.Add($"Missing: {log.CapabilityMissing}");

            if (log.CapabilityStreamingAdjusted)
                lines.Add("Streaming adjusted: off");

            if (!string.IsNullOrWhiteSpace(log.CapabilityReason))
                lines.Add($"Reason: {log.CapabilityReason}");

            if (lines.Count == 0)
                lines.Add("Capability Guard: OK");

            return new NodeDecisionStepViewData
            {
                Title = "Capability Guard",
                Detail = detail,
                State = log.CapabilityAdjusted ? NodeDecisionStepState.Warning : NodeDecisionStepState.Success,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildFallbackStep(AiExecutionLogEntry log, bool apiFallbackUsed)
        {
            string detail = BuildFallbackDetail(log, apiFallbackUsed);
            if (string.IsNullOrWhiteSpace(detail))
                detail = "無";

            var lines = new List<string>();

            if (apiFallbackUsed)
                lines.Add("Resolver fallback: API Auto → Rules");

            if (log.RuntimeFallbackUsed && !string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                lines.Add($"Runtime Summary: {log.RuntimeFallbackSummary}");

            if (log.FallbackAttempts != null && log.FallbackAttempts.Count > 0)
            {
                foreach (var attempt in log.FallbackAttempts)
                {
                    if (attempt == null)
                        continue;

                    string modelLabel = GetModelLabel(attempt.ModelId);
                    string symbol = attempt.Success ? "✅" : "❌";
                    string reason = string.IsNullOrWhiteSpace(attempt.Reason) ? "-" : attempt.Reason;
                    string error = string.IsNullOrWhiteSpace(attempt.ErrorMessage) ? "" : $" / {attempt.ErrorMessage}";

                    lines.Add($"{attempt.AttemptIndex}. {modelLabel} {symbol} / {reason}{error}");
                }
            }

            if (lines.Count == 0)
                lines.Add("No fallback used.");

            return new NodeDecisionStepViewData
            {
                Title = "Fallback",
                Detail = detail,
                State = (log.RuntimeFallbackUsed || apiFallbackUsed)
                    ? NodeDecisionStepState.Warning
                    : NodeDecisionStepState.Success,
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildExecutionStep(AiExecutionLogEntry log)
        {
            string executionDetail = log.Success
                ? $"成功 / {log.DurationMs}ms"
                : $"失敗 / {Safe(log.ErrorMessage)}";

            var lines = new List<string>
            {
                $"StartedAtUtc: {log.StartedAtUtc:yyyy-MM-dd HH:mm:ss}",
                $"EndedAtUtc: {log.EndedAtUtc:yyyy-MM-dd HH:mm:ss}",
                $"Duration: {log.DurationMs} ms",
                $"Success: {log.Success}"
            };

            if (!string.IsNullOrWhiteSpace(log.ErrorMessage))
                lines.Add($"Error: {log.ErrorMessage}");

            return new NodeDecisionStepViewData
            {
                Title = "Execution",
                Detail = executionDetail,
                State = log.Success ? NodeDecisionStepState.Success : NodeDecisionStepState.Error,
                Highlight = !log.Success,
                DetailLines = lines
            };
        }

        private static string BuildTaskSummary(AiExecutionLogEntry log)
        {
            return $"{Safe(log.TaskMode)} / {log.Confidence:0.00} / {log.DurationMs}ms";
        }

        private static string BuildKeywordSummary(AiExecutionLogEntry log)
        {
            if (log?.ResolverKeywords == null || log.ResolverKeywords.Count == 0)
                return "";

            var keywords = log.ResolverKeywords
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (keywords.Count == 0)
                return "";

            return "keywords: " + string.Join(", ", keywords);
        }

        private static string BuildExtraSummary(AiExecutionLogEntry log)
        {
            var extraParts = new List<string>();


            if (!string.IsNullOrWhiteSpace(log.CapabilityReason))
                extraParts.Add(log.CapabilityReason);

            string capabilityDetail = BuildCapabilityDetail(log);
            if (!string.IsNullOrWhiteSpace(capabilityDetail))
                extraParts.Add("capability: " + capabilityDetail);

            if (!string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                extraParts.Add(log.RuntimeFallbackSummary);

            string trace = BuildFallbackTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(trace))
                extraParts.Add(trace);

            if (!log.Success && !string.IsNullOrWhiteSpace(log.ErrorMessage))
                extraParts.Add(log.ErrorMessage);

            return extraParts.Count == 0 ? "" : string.Join(" / ", extraParts);
        }

        private static string BuildCapabilityDetail(AiExecutionLogEntry log)
        {
            if (log == null || !log.CapabilityAdjusted)
                return "";

            string requestedLabel = GetModelLabel(log.CapabilityRequestedModelId);
            string resolvedLabel = GetModelLabel(log.CapabilityResolvedModelId);

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.CapabilityMissing))
                parts.Add($"missing: {log.CapabilityMissing}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequired) &&
                !string.Equals(log.CapabilityRequired, AiModelCapability.None.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"required: {log.CapabilityRequired}");
            }

            if (!string.Equals(requestedLabel, resolvedLabel, StringComparison.OrdinalIgnoreCase))
                parts.Add($"{requestedLabel} → {resolvedLabel}");

            if (log.CapabilityStreamingAdjusted)
                parts.Add("streaming → off");

            return parts.Count == 0 ? "" : string.Join(" / ", parts);
        }

        private static string BuildFallbackDetail(AiExecutionLogEntry log, bool apiFallbackUsed)
        {
            var parts = new List<string>();

            if (apiFallbackUsed)
                parts.Add("resolver fallback");

            if (log.RuntimeFallbackUsed && !string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                parts.Add(log.RuntimeFallbackSummary);

            string trace = BuildFallbackTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(trace))
                parts.Add(trace);

            return parts.Count == 0 ? "" : string.Join(" / ", parts);
        }

        private static string BuildFallbackTraceSummary(AiExecutionLogEntry log)
        {
            if (log?.FallbackAttempts == null || log.FallbackAttempts.Count == 0)
                return "";

            var parts = new List<string>();

            foreach (var attempt in log.FallbackAttempts)
            {
                if (attempt == null || string.IsNullOrWhiteSpace(attempt.ModelId))
                    continue;

                string modelLabel = GetModelLabel(attempt.ModelId);
                string symbol = attempt.Success ? "✅" : "❌";

                parts.Add($"{attempt.AttemptIndex}.{modelLabel}{symbol}");
            }

            if (parts.Count == 0)
                return "";

            return "trace: " + string.Join(" → ", parts);
        }

        private static string GetModelLabel(string modelId)
        {
            var def = AiModelHelper.GetDefinition(modelId);

            if (!string.IsNullOrWhiteSpace(def.DisplayName))
                return def.DisplayName;

            if (!string.IsNullOrWhiteSpace(def.Id))
                return def.Id;

            return AiModelRegistry.Default.DisplayName;
        }

        private static string Safe(string? text)
        {
            return string.IsNullOrWhiteSpace(text) ? "-" : text;
        }
    }
}