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
            bool hasAttachments,
            bool hasImageAttachment = false,
            OutputIntent? outputIntent = null,
            bool allowMediaGeneration = true)
        {
            var taskType = ResolveTaskType(userInput, decision?.TaskMode ?? NodeTaskMode.Chat);

            // 圖片編輯優先覆寫：有圖片附件 + 編輯意圖 → ImageEdit（不論分類器怎麼說）。
            if (hasImageAttachment && IsImageEditIntent(userInput))
                taskType = OrchestrationTaskType.ImageEdit;
            // 第一層 LLM 意圖補強：影片/圖片不再只靠關鍵字白名單。關鍵字沒抓到、但第一層
            // OutputIntentResolver（語意判斷）說要影片/圖片時，以 LLM 為準——和簡報/報告/表格同一真相來源，
            // 修掉「給我一個15秒的影片」這種講法因不在白名單而被當成純文字的漏判。
            else if (outputIntent?.WantsVideo == true && !ProducesMediaOrFile(taskType))
                taskType = OrchestrationTaskType.VideoGeneration;
            else if (outputIntent?.WantsImage == true && !ProducesMediaOrFile(taskType))
                taskType = OrchestrationTaskType.ImageGeneration;

            // 第一層意圖閘門：未獲准產出（純手動模式，或使用者在二次確認框選「否」）時，
            // 把會產生檔案/媒體的任務型別降級為純文字任務——連帶不加任何產出階段，
            // 主合成也不會被影片/簡報的框架污染，使用者選的模型就只做純文字回答。
            if (!allowMediaGeneration && ProducesMediaOrFile(taskType))
                taskType = TextTaskTypeFromMode(decision?.TaskMode ?? NodeTaskMode.Chat);

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

            // §6 多輸出：除了主任務型別，也看使用者是否「同時」要書面報告與簡報。
            // 優先用第一層 LLM 判斷結果（OutputIntentResolver，語意理解，與實際產檔同一真相）；
            // 子代理委派沒有 outputIntent 時才 fallback 關鍵字掃描，使顯示的 stage 與實際產檔一致。
            bool wantsReport = allowMediaGeneration && (outputIntent?.WantsReport ?? OutputFormatDetector.WantsWrittenReport(userInput));
            bool wantsDeck   = allowMediaGeneration && (outputIntent?.WantsPresentation ?? OutputFormatDetector.WantsPresentation(userInput));

            // File Generation v1：GenerateFile 任務（或簡報任務又要求書面報告）在最終答案之後，多一個寫檔階段。
            if (allowMediaGeneration &&
                (taskType == OrchestrationTaskType.GenerateFile ||
                 (taskType == OrchestrationTaskType.Presentation && wantsReport)))
                AddStage(stages, "generate_file", "Generate file", "file-writer");

            // Presentation Agent v1：Presentation 任務（或寫檔任務又要求簡報）在最終答案之後，多一個產生 deck 階段。
            if (allowMediaGeneration &&
                (taskType == OrchestrationTaskType.Presentation ||
                 (taskType == OrchestrationTaskType.GenerateFile && wantsDeck)))
                AddStage(stages, "presentation_outline", "Build presentation outline", "presentation-agent");

            // Image Gen v1：ImageGeneration 任務在最終答案之後，多一個產生圖片階段。
            if (allowMediaGeneration && taskType == OrchestrationTaskType.ImageGeneration)
                AddStage(stages, "generate_image", "Generate image", "image-agent");

            // Image Edit：上傳圖 + 編輯指令 → 改圖階段。
            if (allowMediaGeneration && taskType == OrchestrationTaskType.ImageEdit)
                AddStage(stages, "generate_image_edit", "Edit image", "image-agent");

            // Video Gen v1：VideoGeneration 任務在最終答案之後，多一個（長時間、需輪詢的）產生影片階段。
            if (allowMediaGeneration && taskType == OrchestrationTaskType.VideoGeneration)
                AddStage(stages, "generate_video", "Generate video", "video-agent");

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

        // public：供 MainWindow 在不重跑整個 orchestration 的情況下，重推導任務型別
        // （§4 自動下游節點：用節點 prompt + task mode 判斷是否為多階段任務）。
        public static OrchestrationTaskType ResolveTaskType(string? text, NodeTaskMode mode)
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
                    "export", "docx", "word", "file output", "generate file",
                    "excel", "xlsx", "試算表", "spreadsheet"))
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

            // 影片：必須是「明確要產生影片」的命令式說法，才判為 VideoGeneration。
            // 不可只因內文出現「影片 / video」就觸發——那會把「他發了個影片限動」這種純聊天誤判成要生影片。
            if (ContainsAny(
                    normalized,
                    "生成影片", "產生影片", "做一支影片", "做一部影片", "做個影片", "做成影片",
                    "製作影片", "幫我做影片", "剪一支影片", "剪輯影片", "剪成影片",
                    "拍一支影片", "拍一部影片", "拍個影片", "預告片", "短影片", "音樂影片",
                    "generate video", "make a video", "create a video", "video clip", "trailer", "montage"))
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

            return TextTaskTypeFromMode(mode);
        }

        // 純文字任務型別（不產生任何檔案/媒體），由節點的 TaskMode 推得。
        // 供 ResolveTaskType 的尾段 fallback，以及意圖閘門把產檔/媒體任務降級時共用。
        private static OrchestrationTaskType TextTaskTypeFromMode(NodeTaskMode mode)
        {
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

        // 是否為「會實際產生檔案或媒體」的任務型別（需要二次確認、且純手動模式下要降級的對象）。
        public static bool ProducesMediaOrFile(OrchestrationTaskType taskType)
        {
            return taskType == OrchestrationTaskType.GenerateFile
                || taskType == OrchestrationTaskType.Presentation
                || taskType == OrchestrationTaskType.ImageGeneration
                || taskType == OrchestrationTaskType.ImageEdit
                || taskType == OrchestrationTaskType.VideoGeneration
                || taskType == OrchestrationTaskType.Media;
        }

        // 寬鬆閘門：輸入「有沒有提到」影片/圖片（不分是要產出還是只是描述）。
        // 用途：NodeService 用它決定要不要花一次便宜 API 跑第一層 LLM 精準判斷。
        // 故意寬（高召回）——精準的「是要產出還是只是描述」交給 OutputIntentResolver。
        public static bool MentionsVideoOrImage(string? text)
        {
            string s = (text ?? "").ToLowerInvariant();
            if (s.Length == 0) return false;

            return ContainsAny(s,
                "影片", "短片", "動畫", "影像", "video", "movie", "clip", "animation",
                "圖片", "圖像", "插圖", "圖檔", "畫一", "畫個", "畫張", "image", "picture", "illustration", "draw");
        }

        // 有圖片附件時，使用者是否想「編輯這張圖」（改建 / 裝潢 / 改圖 / 套風格…）。
        public static bool IsImageEditIntent(string? text)
        {
            string s = (text ?? "").ToLowerInvariant();
            if (s.Length == 0) return false;

            return ContainsAny(s,
                "改建", "改造", "翻新", "拉皮", "重新裝潢", "裝潢", "重新設計", "改設計", "重繪", "重畫",
                "改圖", "改這張", "改成", "編輯", "修改", "修圖", "p圖", "ps成", "photoshop", "去背",
                "換成", "換掉", "替換", "重新上色", "上色", "風格化", "套用風格", "依照片", "根據照片", "根據附圖", "根據圖",
                "edit", "redesign", "renovate", "remodel", "inpaint", "restyle", "retouch", "modify");
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
                OrchestrationTaskType.ImageEdit => "image_edit",
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
