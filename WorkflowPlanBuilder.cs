using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class WorkflowPlanBuilder
    {
        public static WorkflowPlanPayload FromOrchestrationPlan(
            OrchestrationPlanPayload plan,
            string sourceNodeId,
            string userInput)
        {
            if (plan == null)
            {
                return new WorkflowPlanPayload
                {
                    SourceNodeId = sourceNodeId ?? "",
                    UserInputPreview = Trim(userInput, 160),
                    Notes = new[] { "No orchestration plan was available." }
                };
            }

            var nodes = new List<WorkflowNodePayload>();
            var edges = new List<WorkflowEdgePayload>();

            string previousNodeId = "";

            foreach (var stage in plan.Stages ?? Array.Empty<OrchestrationStagePayload>())
            {
                if (stage == null)
                    continue;

                string nodeId = string.IsNullOrWhiteSpace(stage.Id)
                    ? $"stage_{stage.Order}"
                    : stage.Id.Trim();

                nodes.Add(new WorkflowNodePayload
                {
                    Id = nodeId,
                    Label = string.IsNullOrWhiteSpace(stage.Label) ? nodeId : stage.Label.Trim(),
                    TaskType = plan.TaskType.ToString(),
                    AgentId = string.IsNullOrWhiteSpace(stage.Owner) ? plan.RuntimeAgentId : stage.Owner,
                    ModelId = plan.ModelId ?? "",
                    CapabilityId = ResolveCapabilityId(stage.Id),
                    InputPreview = ResolveInputPreview(stage, plan, userInput),
                    OutputPreview = ResolveOutputPreview(stage, plan),
                    Status = string.IsNullOrWhiteSpace(stage.Status) ? "planned" : stage.Status.Trim()
                });

                if (!string.IsNullOrWhiteSpace(previousNodeId))
                {
                    edges.Add(new WorkflowEdgePayload
                    {
                        FromNodeId = previousNodeId,
                        ToNodeId = nodeId,
                        Kind = "control"
                    });
                }

                previousNodeId = nodeId;
            }

            return new WorkflowPlanPayload
            {
                Status = "draft",
                PipelineId = plan.PipelineId ?? "",
                TaskType = plan.TaskType,
                SourceNodeId = sourceNodeId ?? "",
                UserInputPreview = Trim(userInput, 160),
                CreatesCanvasNodes = false,
                ReplaySupported = false,
                RerunSupported = false,
                ResumeSupported = false,
                Nodes = nodes,
                Edges = edges,
                Notes = new[]
                {
                    "Workflow schema v1 only records planned nodes and edges.",
                    "It does not create, run, replay, or resume downstream canvas nodes yet."
                }
            };
        }

        private static string ResolveCapabilityId(string stageId)
        {
            if (string.IsNullOrWhiteSpace(stageId))
                return "";

            const string prefix = "capability:";
            return stageId.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                ? stageId.Substring(prefix.Length).Trim()
                : "";
        }

        private static string ResolveInputPreview(
            OrchestrationStagePayload stage,
            OrchestrationPlanPayload plan,
            string userInput)
        {
            string stageId = stage?.Id ?? "";

            if (string.Equals(stageId, "detect_task", StringComparison.OrdinalIgnoreCase))
                return Trim(userInput, 160);

            if (string.Equals(stageId, "select_pipeline", StringComparison.OrdinalIgnoreCase))
                return $"TaskType={plan.TaskType}";

            if (string.Equals(stageId, "select_agent", StringComparison.OrdinalIgnoreCase))
                return $"Pipeline={plan.PipelineId}";

            if (string.Equals(stageId, "select_model", StringComparison.OrdinalIgnoreCase))
                return $"Agent={plan.RuntimeAgentId}";

            if (stageId.StartsWith("capability:", StringComparison.OrdinalIgnoreCase))
                return $"User input + workspace context for {ResolveCapabilityId(stageId)}";

            if (string.Equals(stageId, "write_workspace", StringComparison.OrdinalIgnoreCase))
                return "Capability outputs";

            if (string.Equals(stageId, "final_synthesis", StringComparison.OrdinalIgnoreCase))
                return "Workspace artifacts + original user input";

            return "";
        }

        private static string ResolveOutputPreview(
            OrchestrationStagePayload stage,
            OrchestrationPlanPayload plan)
        {
            string stageId = stage?.Id ?? "";

            if (string.Equals(stageId, "detect_task", StringComparison.OrdinalIgnoreCase))
                return $"TaskType={plan.TaskType}";

            if (string.Equals(stageId, "select_pipeline", StringComparison.OrdinalIgnoreCase))
                return $"Pipeline={plan.PipelineId}";

            if (string.Equals(stageId, "select_agent", StringComparison.OrdinalIgnoreCase))
                return $"Agent={plan.RuntimeAgentId}";

            if (string.Equals(stageId, "select_model", StringComparison.OrdinalIgnoreCase))
                return $"Model={plan.ModelId}";

            if (stageId.StartsWith("capability:", StringComparison.OrdinalIgnoreCase))
                return $"{ResolveCapabilityId(stageId)} artifact(s)";

            if (string.Equals(stageId, "write_workspace", StringComparison.OrdinalIgnoreCase))
                return "Workspace artifact index";

            if (string.Equals(stageId, "final_synthesis", StringComparison.OrdinalIgnoreCase))
                return "Final answer text";

            return "";
        }

        private static string Trim(string text, int maxChars)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            return text.Length <= maxChars
                ? text
                : text.Substring(0, maxChars) + "...";
        }
    }
}
