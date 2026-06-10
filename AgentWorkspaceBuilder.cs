using System;

namespace test
{
    public static class AgentWorkspaceBuilder
    {
        public static AgentWorkspaceItem FromCapabilityData(
            AgentWorkspace workspace,
            NodeControl node,
            string agentId,
            string key,
            object value)
        {
            key ??= "";

            return new AgentWorkspaceItem
            {
                RunId = workspace?.RunId ?? "",
                NodeId = node?.Id.ToString() ?? "",
                SourceAgentId = agentId ?? "",
                ItemType = key,
                ArtifactKind = ResolveArtifactKind(key, value),
                ContentFormat = ResolveContentFormat(key, value),
                IsUserVisible = ResolveUserVisible(key, value),
                Title = BuildTitle(key, value),
                Payload = value,
                TextSummary = BuildTextSummary(value),
                CreatedAtUtc = DateTime.UtcNow
            };
        }

        private static string ResolveArtifactKind(string key, object value)
        {
            key ??= "";

            if (value is VerifiedFactPayload)
                return "facts";

            if (value is SearchSummaryPayload)
                return "search";

            if (value is CodeAnalysisPayload)
                return "code";

            if (value is CodeFileSnapshotPayload)
                return "code";

            if (value is CodeDiffArtifactPayload)
                return "diff";

            if (value is CodeDiffValidationPayload)
                return "sandbox";

            if (value is ReasoningPayload ||
                value is TaskDecompositionPayload ||
                value is DelegateOutputPayload)
            {
                return "analysis";
            }

            if (value is OrchestrationPlanPayload)
                return "workflow";

            if (value is FinalSynthesisPayload)
                return "final";

            if (key.Contains("patch", StringComparison.OrdinalIgnoreCase))
                return "patch";

            if (key.Contains("diff", StringComparison.OrdinalIgnoreCase))
                return "diff";

            if (key.Contains("image", StringComparison.OrdinalIgnoreCase))
                return "media";

            if (key.Contains("video", StringComparison.OrdinalIgnoreCase))
                return "media";

            if (key.Contains("sandbox", StringComparison.OrdinalIgnoreCase))
                return "sandbox";

            if (value is FileSummaryPayload)
                return "file";

            return "artifact";
        }

        private static string ResolveContentFormat(string key, object value)
        {
            key ??= "";

            if (key.Contains("image", StringComparison.OrdinalIgnoreCase))
                return "image";

            if (key.Contains("video", StringComparison.OrdinalIgnoreCase))
                return "video";

            if (key.Contains("patch", StringComparison.OrdinalIgnoreCase))
                return "patch";

            if (key.Contains("diff", StringComparison.OrdinalIgnoreCase))
                return "diff";

            if (value is CodeAnalysisPayload)
                return "code";

            if (value is CodeFileSnapshotPayload)
                return "code";

            if (value is CodeDiffArtifactPayload)
                return "diff";

            if (value is CodeDiffValidationPayload)
                return "markdown";

            if (value is VerifiedFactPayload ||
                value is SearchSummaryPayload ||
                value is FileSummaryPayload ||
                value is ReasoningPayload ||
                value is TaskDecompositionPayload ||
                value is OrchestrationPlanPayload ||
                value is FinalSynthesisPayload ||
                value is DelegateOutputPayload)
            {
                return "markdown";
            }

            return "text";
        }

        private static bool ResolveUserVisible(string key, object value)
        {
            key ??= "";

            if (value is DelegateOutputPayload ||
                string.Equals(key, "parallel_agent_output", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(key, "delegate_output", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return true;
        }

        private static string BuildTitle(string key, object value)
        {
            key ??= "";

            if (string.Equals(key, "parallel_agent_output", StringComparison.OrdinalIgnoreCase) &&
                value is DelegateOutputPayload parallel)
            {
                string model = string.IsNullOrWhiteSpace(parallel.ActualModelId)
                    ? "-"
                    : parallel.ActualModelId;

                return $"Parallel Output - {parallel.ToAgentId} / Model: {model}";
            }
            if (value is VerifiedFactPayload verified)
                return $"Verified Facts - {verified.Query}";
            if (value is SearchSummaryPayload search)
                return $"Search Summary - {search.Query}";

            if (value is FileSummaryPayload)
                return "File Summary";

            if (value is CodeAnalysisPayload code)
                return $"Code Analysis - {code.RequestType}";

            if (value is CodeFileSnapshotPayload snapshot)
                return $"Code File Snapshot - {snapshot.Files?.Count ?? 0} file(s)";

            if (value is CodeDiffArtifactPayload diff)
                return string.IsNullOrWhiteSpace(diff.Title) ? $"Code Diff - {diff.Status}" : diff.Title;

            if (value is CodeDiffValidationPayload validation)
                return $"Code Diff Validation - {validation.Status}";

            if (value is ReasoningPayload reasoning)
                return $"Reasoning - {reasoning.ReasoningType}";

            if (value is TaskDecompositionPayload)
                return "Task Plan";

            if (value is OrchestrationPlanPayload orchestration)
                return $"Orchestration Plan - {orchestration.PipelineId}";

            if (value is FinalSynthesisPayload final)
            {
                string model = string.IsNullOrWhiteSpace(final.ModelId)
                    ? "-"
                    : final.ModelId;

                return $"Final Synthesis - {final.SynthesizerAgentId} / Model: {model}";
            }
            if (value is DelegateOutputPayload d)
            {
                string model = string.IsNullOrWhiteSpace(d.ActualModelId)
                    ? "-"
                    : d.ActualModelId;

                return $"Delegate Output - {d.ToAgentId} / Model: {model}";
            }

            return key;
        }

        private static string BuildTextSummary(object value)
        {
            if (value is VerifiedFactPayload verified)
                return $"Verified Facts - {verified.Query}";
            if (value is SearchSummaryPayload search)
                return search.Summary ?? "";

            if (value is FileSummaryPayload file)
                return file.Summary ?? "";

            if (value is CodeAnalysisPayload code)
                return code.Guidance ?? "";

            if (value is CodeFileSnapshotPayload snapshot)
                return snapshot.Summary ?? "";

            if (value is CodeDiffArtifactPayload diff)
            {
                int fileCount = diff.Files?.Count ?? 0;
                return $"Status={diff.Status}, Files={fileCount}, +{CountAddedLines(diff)}/-{CountRemovedLines(diff)}";
            }

            if (value is CodeDiffValidationPayload validation)
                return validation.Summary ?? "";

            if (value is ReasoningPayload reasoning)
                return reasoning.OutputGuidance ?? "";

            if (value is TaskDecompositionPayload task)
                return task.Summary ?? "";

            if (value is OrchestrationPlanPayload orchestration)
            {
                int stageCount = orchestration.Stages?.Count ?? 0;
                return $"TaskType={orchestration.TaskType}, Pipeline={orchestration.PipelineId}, Stages={stageCount}, Status={orchestration.Status}";
            }

            if (value is FinalSynthesisPayload final)
            {
                string model = string.IsNullOrWhiteSpace(final.ModelId)
                    ? "-"
                    : final.ModelId;

                return $"Synthesizer={final.SynthesizerAgentId}, Model={model}, Success={final.Success}";
            }
            if (value is DelegateOutputPayload d)
            {
                string model = string.IsNullOrWhiteSpace(d.ActualModelId)
                    ? "-"
                    : d.ActualModelId;

                return
                    $"From={d.FromAgentId}, " +
                    $"To={d.ToAgentId}, " +
                    $"Model={model}, " +
                    $"Success={d.Success}";
            }

            return value?.ToString() ?? "";
        }

        private static int CountAddedLines(CodeDiffArtifactPayload diff)
        {
            int total = 0;

            if (diff?.Files == null)
                return total;

            foreach (var file in diff.Files)
                total += file?.AddedLines ?? 0;

            return total;
        }

        private static int CountRemovedLines(CodeDiffArtifactPayload diff)
        {
            int total = 0;

            if (diff?.Files == null)
                return total;

            foreach (var file in diff.Files)
                total += file?.RemovedLines ?? 0;

            return total;
        }
    }
}
