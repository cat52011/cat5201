using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class OrchestrationPlanner
    {
        public static OrchestrationPlanPayload Build(
            string userInput,
            NodeExecutionDecision decision,
            AgentDefinition runtimeAgent,
            AgentCapabilityExecutionPlan capabilityPlan,
            bool autoMode,
            bool hasAttachments)
        {
            var taskType = ResolveTaskType(userInput, decision?.TaskMode ?? NodeTaskMode.Chat);
            string pipelineId = ResolvePipelineId(taskType, hasAttachments);

            var stages = new List<OrchestrationStagePayload>();
            AddStage(stages, "detect_task", "Detect task", "orchestrator");
            AddStage(stages, "select_pipeline", $"Select pipeline: {pipelineId}", "orchestrator");
            AddStage(stages, "select_agent", "Select agent", "orchestrator");
            AddStage(stages, "select_model", "Select model", "model-router");

            if (capabilityPlan?.OrderedCapabilityIds != null)
            {
                foreach (var capabilityId in capabilityPlan.OrderedCapabilityIds)
                {
                    if (!string.IsNullOrWhiteSpace(capabilityId))
                        AddStage(stages, $"capability:{capabilityId}", $"Run {capabilityId}", runtimeAgent?.Id ?? "");
                }
            }

            AddStage(stages, "write_workspace", "Write workspace", "workspace");
            AddStage(stages, "final_synthesis", "Final synthesis", runtimeAgent?.Id ?? "");

            return new OrchestrationPlanPayload
            {
                Status = "planned",
                TaskType = taskType,
                PipelineId = pipelineId,
                TaskMode = (decision?.TaskMode ?? NodeTaskMode.Chat).ToString(),
                RequestedAgentId = decision?.RequestedAgentId ?? "",
                RuntimeAgentId = runtimeAgent?.Id ?? decision?.ActualAgentId ?? "",
                ModelId = decision?.ModelId ?? "",
                AutoMode = autoMode,
                HasAttachments = hasAttachments,
                RequiresFreshFacts = capabilityPlan?.RequiresFreshFacts ?? false,
                CapabilityOrder = capabilityPlan?.OrderedCapabilityIds?.ToList() ?? new List<string>(),
                RequiredCapabilities = capabilityPlan?.RequiredCapabilityIds?.ToList() ?? new List<string>(),
                Stages = stages,
                Reason = BuildReason(taskType, pipelineId, capabilityPlan)
            };
        }

        private static OrchestrationTaskType ResolveTaskType(string? text, NodeTaskMode mode)
        {
            string normalized = (text ?? "").Trim().ToLowerInvariant();

            if (ContainsAny(normalized, "簡報", "投影片", "ppt", "pptx", "slides", "slide deck"))
                return OrchestrationTaskType.Presentation;

            if (ContainsAny(normalized, "pdf", "文件", "報告", "匯出", "輸出成", "export", "docx", "word"))
                return OrchestrationTaskType.GenerateFile;

            if (ContainsAny(normalized, "圖片", "生成圖", "畫一張", "image", "generate image", "繪圖"))
                return OrchestrationTaskType.ImageGeneration;

            if (ContainsAny(normalized, "影片", "生成影片", "video", "generate video"))
                return OrchestrationTaskType.VideoGeneration;

            if (ContainsAny(normalized, "自動", "下游節點", "工作流", "流程", "workflow", "pipeline"))
                return OrchestrationTaskType.Workflow;

            if (ContainsAny(normalized, "計畫", "規劃", "讀書計畫", "學習計畫", "安排", "行動計畫", "plan", "planning", "schedule"))
                return OrchestrationTaskType.Planning;

            return mode switch
            {
                NodeTaskMode.Research => OrchestrationTaskType.Research,
                NodeTaskMode.Summarize => OrchestrationTaskType.Summarize,
                NodeTaskMode.Translate => OrchestrationTaskType.Translate,
                NodeTaskMode.Rewrite => OrchestrationTaskType.Rewrite,
                NodeTaskMode.Extract => OrchestrationTaskType.Extract,
                NodeTaskMode.Code => OrchestrationTaskType.Code,
                _ => OrchestrationTaskType.Chat
            };
        }

        private static string ResolvePipelineId(OrchestrationTaskType taskType, bool hasAttachments)
        {
            return taskType switch
            {
                OrchestrationTaskType.Research => "research_first",
                OrchestrationTaskType.Planning => "planning",
                OrchestrationTaskType.Presentation => "presentation",
                OrchestrationTaskType.GenerateFile => hasAttachments ? "attachment_to_file" : "generate_file",
                OrchestrationTaskType.ImageGeneration => "image_generation",
                OrchestrationTaskType.VideoGeneration => "video_generation",
                OrchestrationTaskType.Media => "media_generation",
                OrchestrationTaskType.Workflow => "auto_workflow",
                OrchestrationTaskType.Code => hasAttachments ? "code_attachment" : "code_generation",
                OrchestrationTaskType.Summarize => hasAttachments ? "attachment_summary" : "summarize",
                OrchestrationTaskType.Translate => "translate",
                OrchestrationTaskType.Rewrite => "rewrite",
                OrchestrationTaskType.Extract => hasAttachments ? "attachment_extract" : "extract",
                _ => hasAttachments ? "attachment_chat" : "chat"
            };
        }

        private static string BuildReason(
            OrchestrationTaskType taskType,
            string pipelineId,
            AgentCapabilityExecutionPlan? capabilityPlan)
        {
            string capabilityReason = string.IsNullOrWhiteSpace(capabilityPlan?.Reason)
                ? ""
                : $" / CapabilityPlan: {capabilityPlan.Reason}";

            return $"TaskType={taskType}; Pipeline={pipelineId}{capabilityReason}";
        }

        private static void AddStage(
            List<OrchestrationStagePayload> stages,
            string id,
            string label,
            string owner)
        {
            stages.Add(new OrchestrationStagePayload
            {
                Order = stages.Count + 1,
                Id = id,
                Label = label,
                Owner = owner,
                Status = "planned"
            });
        }

        private static bool ContainsAny(string text, params string[] needles)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return needles.Any(needle =>
                !string.IsNullOrWhiteSpace(needle) &&
                text.Contains(needle.ToLowerInvariant(), StringComparison.Ordinal));
        }
    }
}
