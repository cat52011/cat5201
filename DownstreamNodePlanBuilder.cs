using System;
using System.Collections.Generic;

namespace test
{
    public static class DownstreamNodePlanBuilder
    {
        public static DownstreamNodePlanPayload FromWorkflowPlan(WorkflowPlanPayload workflow)
        {
            if (workflow == null)
            {
                return new DownstreamNodePlanPayload
                {
                    Notes = new[] { "No workflow plan was available for downstream node planning." }
                };
            }

            var nodes = ResolveProposedNodes(workflow);
            var edges = new List<DownstreamNodeEdgeProposalPayload>();

            for (int i = 1; i < nodes.Count; i++)
            {
                edges.Add(new DownstreamNodeEdgeProposalPayload
                {
                    FromNodeId = nodes[i - 1].Id,
                    ToNodeId = nodes[i].Id
                });
            }

            return new DownstreamNodePlanPayload
            {
                Status = "proposal",
                PipelineId = workflow.PipelineId ?? "",
                TaskType = workflow.TaskType,
                SourceNodeId = workflow.SourceNodeId ?? "",
                CreatesCanvasNodes = false,
                ProposedNodes = nodes,
                ProposedEdges = edges,
                Notes = new[]
                {
                    "This is a downstream canvas node proposal only.",
                    "It does not create, connect, run, replay, rerun, or resume canvas nodes yet."
                }
            };
        }

        private static IReadOnlyList<DownstreamNodeProposalPayload> ResolveProposedNodes(
            WorkflowPlanPayload workflow)
        {
            return workflow.TaskType switch
            {
                OrchestrationTaskType.Presentation => new[]
                {
                    Node("research", "Research source facts", "research-agent", "search-capability", "Original prompt", "Verified research facts"),
                    Node("outline", "Create slide outline", "presentation-agent", "task-planning-capability", "Verified facts", "Slide outline artifact"),
                    Node("deck", "Generate presentation deck", "presentation-agent", "presentation-generation-capability", "Slide outline", "PPTX artifact"),
                    Node("export", "Export deck", "file-agent", "file-generation-capability", "PPTX artifact", "Exported file artifact")
                },
                OrchestrationTaskType.GenerateFile => new[]
                {
                    Node("research", "Collect supporting facts", "research-agent", "search-capability", "Original prompt", "Source-backed facts"),
                    Node("draft", "Draft document", "file-agent", "document-generation-capability", "Facts and user format request", "Document draft artifact"),
                    Node("export", "Export file", "file-agent", "file-generation-capability", "Document draft", "PDF/DOCX/TXT artifact")
                },
                OrchestrationTaskType.Workflow => new[]
                {
                    Node("decompose", "Decompose workflow", "workflow-agent", "task-planning-capability", "Original prompt", "Executable workflow plan"),
                    Node("execute", "Execute workflow steps", "workflow-agent", "workflow-execution-capability", "Workflow plan", "Step artifacts"),
                    Node("synthesize", "Synthesize results", "general-agent", "reasoning-capability", "Step artifacts", "Final answer")
                },
                OrchestrationTaskType.ImageGeneration => new[]
                {
                    Node("prompt", "Refine image prompt", "media-agent", "task-planning-capability", "Original prompt", "Image generation prompt"),
                    Node("image", "Generate image", "media-agent", "image-generation-capability", "Image prompt", "Image artifact")
                },
                OrchestrationTaskType.VideoGeneration => new[]
                {
                    Node("brief", "Create video brief", "media-agent", "task-planning-capability", "Original prompt", "Video generation brief"),
                    Node("video", "Generate video", "media-agent", "video-generation-capability", "Video brief", "Video artifact")
                },
                _ => Array.Empty<DownstreamNodeProposalPayload>()
            };
        }

        private static DownstreamNodeProposalPayload Node(
            string id,
            string label,
            string agentId,
            string capabilityId,
            string inputSource,
            string expectedOutput)
        {
            return new DownstreamNodeProposalPayload
            {
                Id = id,
                Label = label,
                AgentId = agentId,
                CapabilityId = capabilityId,
                InputSource = inputSource,
                ExpectedOutput = expectedOutput,
                Status = "proposed"
            };
        }
    }
}
