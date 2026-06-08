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
                Title = BuildTitle(key, value),
                Payload = value,
                TextSummary = BuildTextSummary(value),
                CreatedAtUtc = DateTime.UtcNow
            };
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

            if (value is ReasoningPayload reasoning)
                return $"Reasoning - {reasoning.ReasoningType}";

            if (value is TaskDecompositionPayload)
                return "Task Plan";
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

            if (value is ReasoningPayload reasoning)
                return reasoning.OutputGuidance ?? "";

            if (value is TaskDecompositionPayload task)
                return task.Summary ?? "";
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
    }
}