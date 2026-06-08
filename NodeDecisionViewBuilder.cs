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

            string capabilitySummary = BuildCapabilityTraceSummary(log);
            if (string.IsNullOrWhiteSpace(capabilitySummary))
                capabilitySummary = "-";

            var capabilityDetails = BuildCapabilityTraceDetails(log);

            string delegationSummary = "-";
            var delegationDetails = new List<string>();
            // 歷史 log 目前若尚未存 delegation trace，可先留空
            // 若你之後把 delegation trace 也加進 log，可在這裡一併接回

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
                Agent = agent,
                Model = modelLabel,
                TaskSummary = taskSummary,
                Reason = reason,
                Keywords = keywords,
                Extra = extra,

                CapabilitySummary = capabilitySummary,
                CapabilityDetails = capabilityDetails,

                DelegationSummary = delegationSummary,
                DelegationDetails = delegationDetails,

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
                CapabilitySummary = "-",
                CapabilityDetails = Array.Empty<string>(),
                DelegationSummary = "-",
                DelegationDetails = Array.Empty<string>(),
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
            steps.Add(BuildWorkspaceStep(log));
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
            IReadOnlyList<string> lines;

            var traceSummary = BuildCapabilityTraceSummary(log);
            var traceDetails = BuildCapabilityTraceDetails(log);

            if (!string.IsNullOrWhiteSpace(traceSummary) && traceSummary != "-")
            {
                detail = traceSummary;
                lines = traceDetails.Count > 0
                    ? traceDetails
                    : BuildCapabilityGuardLines(log);
            }
            else
            {
                detail = log.CapabilityAdjusted
                    ? $"{GetModelLabel(log.CapabilityRequestedModelId)} → {GetModelLabel(log.CapabilityResolvedModelId)}"
                    : "OK";

                lines = BuildCapabilityGuardLines(log);
            }

            var state =
                (!string.IsNullOrWhiteSpace(traceSummary) && traceSummary != "-") || log.CapabilityAdjusted
                    ? NodeDecisionStepState.Warning
                    : NodeDecisionStepState.Success;

            return new NodeDecisionStepViewData
            {
                Title = "Capability",
                Detail = detail,
                State = state,
                Highlight = !string.IsNullOrWhiteSpace(traceSummary) && traceSummary != "-",
                DetailLines = lines
            };
        }

        private static NodeDecisionStepViewData BuildWorkspaceStep(AiExecutionLogEntry log)
        {
            var detailLines = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.WorkspaceSummary))
            {
                detailLines.AddRange(
                    log.WorkspaceSummary
                        .Replace("\r\n", "\n")
                        .Replace('\r', '\n')
                        .Split('\n')
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim()));
            }

            if (log.WorkspaceArtifactDetails != null && log.WorkspaceArtifactDetails.Count > 0)
            {
                if (detailLines.Count > 0)
                    detailLines.Add("--- Artifacts ---");

                detailLines.AddRange(log.WorkspaceArtifactDetails.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            string detail = "-";
            var state = NodeDecisionStepState.Info;

            if (detailLines.Count == 0)
            {
                detail = "No workspace artifacts";
            }
            else
            {
                var artifactCount = log.WorkspaceArtifactDetails?
                    .Count(x => x.StartsWith("Artifact:", StringComparison.OrdinalIgnoreCase)) ?? 0;

                var factCount = log.WorkspaceArtifactDetails?
                    .Count(x => x.TrimStart().StartsWith("Fact:", StringComparison.OrdinalIgnoreCase)) ?? 0;

                detail = $"Artifacts: {artifactCount}, facts: {factCount}";
                state = factCount > 0 ? NodeDecisionStepState.Success : NodeDecisionStepState.Info;
            }

            return new NodeDecisionStepViewData
            {
                Title = "Workspace",
                Detail = detail,
                State = state,
                Highlight = detailLines.Count > 0,
                DetailLines = detailLines
            };
        }

        private static IReadOnlyList<string> BuildCapabilityGuardLines(AiExecutionLogEntry log)
        {
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

            return lines;
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

        private static string BuildCapabilityTraceSummary(AiExecutionLogEntry log)
        {
            if (log?.CapabilityTrace == null || log.CapabilityTrace.Count == 0)
                return "";

            return AgentCapabilityTraceFormatter.BuildSummary(log.CapabilityTrace);
        }

        private static IReadOnlyList<string> BuildCapabilityTraceDetails(AiExecutionLogEntry log)
        {
            if (log?.CapabilityTrace == null || log.CapabilityTrace.Count == 0)
                return new List<string>();

            return AgentCapabilityTraceFormatter.BuildDetailLines(log.CapabilityTrace);
        }
        private static string Trim(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }

        private static string BuildExtraSummary(AiExecutionLogEntry log)
        {
            var extraParts = new List<string>();

            string capabilityTraceSummary = BuildCapabilityTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(capabilityTraceSummary) && capabilityTraceSummary != "-")
                extraParts.Add("capability: " + capabilityTraceSummary);

            if (!string.IsNullOrWhiteSpace(log.CapabilityReason))
                extraParts.Add(log.CapabilityReason);

            string capabilityDetail = BuildCapabilityDetail(log);
            if (!string.IsNullOrWhiteSpace(capabilityDetail))
                extraParts.Add("capability-guard: " + capabilityDetail);

            if (!string.IsNullOrWhiteSpace(log.RuntimeFallbackSummary))
                extraParts.Add(log.RuntimeFallbackSummary);

            string trace = BuildFallbackTraceSummary(log);
            if (!string.IsNullOrWhiteSpace(trace))
                extraParts.Add(trace);

            if (!log.Success && !string.IsNullOrWhiteSpace(log.ErrorMessage))
                extraParts.Add(log.ErrorMessage);

            if (!string.IsNullOrWhiteSpace(log.WorkspaceSummary))
                extraParts.Add("多代理協作: " + Trim(log.WorkspaceSummary, 360));


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

        private static string GetModelLabel(string? modelId)
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
