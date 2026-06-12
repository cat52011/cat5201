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

            // File Generation v1：GenerateFile 任務在最終答案之後，多一個寫檔階段。
            if (taskType == OrchestrationTaskType.GenerateFile)
                AddStage(stages, "generate_file", "Generate file", "file-writer");

            // Presentation Agent v1：Presentation 任務在最終答案之後，多一個產生投影片大綱 / deck 階段。
            if (taskType == OrchestrationTaskType.Presentation)
                AddStage(stages, "presentation_outline", "Build presentation outline", "presentation-agent");

            // Image Gen v1：ImageGeneration 任務在最終答案之後，多一個產生圖片階段。
            if (taskType == OrchestrationTaskType.ImageGeneration)
                AddStage(stages, "generate_image", "Generate image", "image-agent");

            return new OrchestrationPlanPayload
            {
                Status = "pending",
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

            if (ContainsAny(
                    normalized,
                    "簡報", "投影片", "做成ppt", "做成 ppt", "生成ppt", "生成 ppt",
                    "ppt", "pptx", "slides", "slide deck", "presentation"))
            {
                return OrchestrationTaskType.Presentation;
            }

            if (ContainsAny(
                    normalized,
                    "pdf", "文件", "文檔", "報告", "匯出", "輸出成", "生成檔案",
                    "export", "docx", "word", "file output", "generate file"))
            {
                return OrchestrationTaskType.GenerateFile;
            }

            if (ContainsAny(
                    normalized,
                    "圖片", "圖像", "生成圖片", "產生圖片",
                    "畫一張", "畫一隻", "畫一幅", "畫個", "畫張", "幫我畫", "請畫",
                    "image", "generate image", "draw"))
            {
                return OrchestrationTaskType.ImageGeneration;
            }

            if (ContainsAny(
                    normalized,
                    "影片", "視頻", "生成影片", "產生影片",
                    "video", "generate video"))
            {
                return OrchestrationTaskType.VideoGeneration;
            }

            if (ContainsAny(
                    normalized,
                    "工作流", "自動流程", "自動生成下游節點", "下游節點", "自動往下跑",
                    "workflow", "pipeline", "downstream node", "auto flow"))
            {
                return OrchestrationTaskType.Workflow;
            }

            if (ContainsAny(
                    normalized,
                    "計畫", "規劃", "安排", "排程", "讀書計畫", "訓練計畫", "學習計畫",
                    "plan", "planning", "schedule"))
            {
                return OrchestrationTaskType.Planning;
            }

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
                Status = "pending"
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
