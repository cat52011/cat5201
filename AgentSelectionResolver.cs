using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public sealed class AgentSelectionResolver
    {
        public AgentSelectionResolution Resolve(
            string topText,
            NodeTaskMode taskMode,
            IReadOnlyList<MainWindow.AttachmentInfo> attachments,
            string? fallbackAgentId = null)
        {
            topText ??= "";
            attachments ??= Array.Empty<MainWindow.AttachmentInfo>();

            bool hasImageAttachments = attachments.Any(a =>
                string.Equals(a.Kind, "image", StringComparison.OrdinalIgnoreCase));

            bool hasFileAttachments = attachments.Any(a =>
                !string.Equals(a.Kind, "image", StringComparison.OrdinalIgnoreCase));

            string normalizedFallback = AgentRegistry.IsKnown(fallbackAgentId)
                ? AgentRegistry.Get(fallbackAgentId).Id
                : AgentRegistry.Default.Id;

            // 圖片生成通常以 Chat mode 進來（NodeTaskMode 沒有 Image），需在 switch 前先攔，
            // 否則會落到 general-agent。關鍵詞需與 OrchestrationPlanner.ResolveTaskType 的
            // ImageGeneration 清單保持一致。
            if (!hasImageAttachments && IsImageRequest(topText))
            {
                return new AgentSelectionResolution
                {
                    AgentId = "image-agent",
                    Confidence = 0.93,
                    Reason = "偵測到圖片生成需求，優先使用 image-agent"
                };
            }

            switch (NodeTaskModeHelper.Normalize(taskMode))
            {
                case NodeTaskMode.Translate:
                    return new AgentSelectionResolution
                    {
                        AgentId = "translation-agent",
                        Confidence = hasImageAttachments || hasFileAttachments ? 0.98 : 0.95,
                        Reason = hasImageAttachments || hasFileAttachments
                            ? "偵測到翻譯任務，且含附件，優先使用 translation-agent"
                            : "偵測到翻譯任務，優先使用 translation-agent"
                    };

                case NodeTaskMode.Code:
                    return new AgentSelectionResolution
                    {
                        AgentId = "code-agent",
                        Confidence = hasFileAttachments ? 0.97 : 0.94,
                        Reason = hasFileAttachments
                            ? "偵測到程式任務，且含檔案附件，優先使用 code-agent"
                            : "偵測到程式任務，優先使用 code-agent"
                    };

                case NodeTaskMode.Research:
                    return new AgentSelectionResolution
                    {
                        AgentId = "research-agent",
                        Confidence = 0.92,
                        Reason = "偵測到查證 / 搜尋 / 比較分析需求，優先使用 research-agent"
                    };

                case NodeTaskMode.Summarize:
                case NodeTaskMode.Rewrite:
                case NodeTaskMode.Extract:
                case NodeTaskMode.Chat:
                default:
                    return new AgentSelectionResolution
                    {
                        AgentId = "general-agent",
                        Confidence = string.IsNullOrWhiteSpace(topText) ? 0.35 : 0.80,
                        Reason = string.IsNullOrWhiteSpace(topText)
                            ? $"內容不足，先回退至 {normalizedFallback}"
                            : "目前任務屬一般整理 / 對話 / 泛用型需求，優先使用 general-agent"
                    };
            }
        }

        // 與 OrchestrationPlanner.ResolveTaskType 的 ImageGeneration 關鍵詞一致。
        private static bool IsImageRequest(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string lower = text.ToLowerInvariant();
            string[] needles =
            {
                "圖片", "圖像", "生成圖片", "產生圖片",
                "畫一張", "畫一隻", "畫一幅", "畫個", "畫張", "幫我畫", "請畫",
                "image", "generate image", "draw"
            };

            foreach (var needle in needles)
            {
                if (lower.Contains(needle, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }
    }
}