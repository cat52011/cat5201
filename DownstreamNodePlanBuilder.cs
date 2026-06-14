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

            var nodes = ResolveProposedNodes(workflow.TaskType);
            var edges = BuildChainEdges(nodes);

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
                    "Materialize it to create, connect, and (optionally) auto-run the workflow nodes."
                }
            };
        }

        /// <summary>
        /// §4：直接由任務型別建出「可實際執行」的下游節點計畫（不需先跑 workflow plan）。
        /// 所有節點都對應 AgentRegistry 內真實存在、可執行的 agent，
        /// 故 MaterializeDownstreamNodePlan 不會標成 unsupported、自動鏈不會中途停。
        /// </summary>
        public static DownstreamNodePlanPayload FromTaskType(
            OrchestrationTaskType taskType,
            string sourceNodeId,
            string pipelineId = "")
        {
            var nodes = ResolveProposedNodes(taskType);
            var edges = BuildChainEdges(nodes);

            return new DownstreamNodePlanPayload
            {
                Status = "proposal",
                PipelineId = pipelineId ?? "",
                TaskType = taskType,
                SourceNodeId = sourceNodeId ?? "",
                CreatesCanvasNodes = true,
                ProposedNodes = nodes,
                ProposedEdges = edges,
                Notes = new[]
                {
                    "Executable downstream workflow built from task type.",
                    "Each node maps to a real agent so the chain can run end-to-end."
                }
            };
        }

        private static List<DownstreamNodeEdgeProposalPayload> BuildChainEdges(
            IReadOnlyList<DownstreamNodeProposalPayload> nodes)
        {
            var edges = new List<DownstreamNodeEdgeProposalPayload>();
            if (nodes == null)
                return edges;

            for (int i = 1; i < nodes.Count; i++)
            {
                edges.Add(new DownstreamNodeEdgeProposalPayload
                {
                    FromNodeId = nodes[i - 1].Id,
                    ToNodeId = nodes[i].Id
                });
            }

            return edges;
        }

        // 重點：只用 AgentRegistry 內真實 agent（general-agent / research-agent / image-agent / code-agent）。
        // 各節點實際做什麼，由 BuildDownstreamNodePrompt 產生的「帶關鍵字指令」在執行時觸發對應 orchestration
        // （例如帶「簡報」→ Presentation、帶「輸出成檔案/報告」→ GenerateFile、帶「圖片」→ ImageGeneration）。
        private static IReadOnlyList<DownstreamNodeProposalPayload> ResolveProposedNodes(
            OrchestrationTaskType taskType)
        {
            return taskType switch
            {
                // 簡報：不展開成下游鏈。母節點本身已是完整的「研究→撰寫→出檔」管線，自己就產出一份完整 deck；
                // 若再拆成 research→outline→deck 三個節點，每個節點的提示都帶「簡報」關鍵字，會各自把整條管線
                // 從頭重跑一遍，產出三份各自獨立又淺薄的檔。因此簡報維持「單一節點、出一份」。
                OrchestrationTaskType.Presentation => System.Array.Empty<DownstreamNodeProposalPayload>(),
                OrchestrationTaskType.GenerateFile => new[]
                {
                    Node("research", "Collect supporting facts", "research-agent", "search-capability", "Original prompt", "Source-backed facts"),
                    Node("draft", "Draft document", "general-agent", "document-generation-capability", "Facts and user request", "Document draft"),
                    Node("export", "Export file", "general-agent", "file-generation-capability", "Document draft", "Exported file")
                },
                OrchestrationTaskType.ImageGeneration => new[]
                {
                    Node("prompt", "Refine image prompt", "general-agent", "task-planning-capability", "Original prompt", "Image generation prompt"),
                    Node("image", "Generate image", "image-agent", "image-generation-capability", "Image prompt", "Image file")
                },
                OrchestrationTaskType.VideoGeneration => new[]
                {
                    Node("brief", "Create video brief", "general-agent", "task-planning-capability", "Original prompt", "Video script and storyboard"),
                    Node("video", "Generate video", "general-agent", "video-generation-capability", "Video brief", "Video plan / file")
                },
                OrchestrationTaskType.Workflow => new[]
                {
                    Node("research", "Gather inputs", "research-agent", "search-capability", "Original prompt", "Source facts"),
                    Node("draft", "Process and analyze", "general-agent", "reasoning-capability", "Source facts", "Analyzed content"),
                    Node("synthesize", "Synthesize results", "general-agent", "reasoning-capability", "Analyzed content", "Final answer")
                },
                OrchestrationTaskType.Research => new[]
                {
                    Node("research", "Research the topic", "research-agent", "search-capability", "Original prompt", "Verified facts"),
                    Node("synthesize", "Synthesize findings", "general-agent", "reasoning-capability", "Verified facts", "Final summary")
                },
                OrchestrationTaskType.Planning => new[]
                {
                    Node("research", "Collect background facts", "research-agent", "search-capability", "Original prompt", "Background facts"),
                    Node("draft", "Build the plan", "general-agent", "task-planning-capability", "Background facts", "Structured plan")
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
