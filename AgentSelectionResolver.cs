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
    }
}