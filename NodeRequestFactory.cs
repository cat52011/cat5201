using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class NodeRequestFactory
    {
        private readonly MainWindow _main;
        private readonly AiServiceRouter _router;

        public NodeRequestFactory(MainWindow main, AiServiceRouter router)
        {
            _main = main;
            _router = router;
        }

        public Task<AiRequest> BuildAsync(
            NodeControl currentNode,
            string model,
            string instructions,
            string userPrompt,
            NodeTaskMode taskMode,
            bool useStreaming,
            int maxOutputTokens,
            CancellationToken ct)
        {
            // 個人化開關（IsReadUpstreamAttachmentsEnabled）：
            //  關（預設）→ 只帶本節點自己的附件（較省）；下游只靠上游的文字輸出。
            //  開 → 沿上游鏈繼承附件（GetEffectiveAttachmentsForNode），下游看得到母節點掛的原始檔案，但較貴。
            var sourceAttachments = _main.IsReadUpstreamAttachmentsEnabled()
                ? _main.GetEffectiveAttachmentsForNode(currentNode)
                : _main.GetAttachmentsForNode(currentNode);

            var attachments = sourceAttachments
                .Select(a => new AiAttachment
                {
                    FileName = a.FileName ?? "",
                    RelativePath = a.RelativePath ?? "",
                    AbsolutePath = ResolveAbsoluteAttachmentPath(a.RelativePath),
                    MimeType = NormalizeAttachmentMimeType(a.FileName, a.MimeType),
                    Kind = string.IsNullOrWhiteSpace(a.Kind) ? "file" : a.Kind
                })
                .Where(a => !string.IsNullOrWhiteSpace(a.AbsolutePath))
                .ToList();

            return Task.FromResult(new AiRequest
            {
                ModelId = AiModelHelper.NormalizeNodeModel(model),
                SystemPrompt = instructions ?? "",
                UserPrompt = userPrompt ?? "",
                TaskMode = NodeTaskModeHelper.Normalize(taskMode),
                Attachments = attachments,
                UseStreaming = useStreaming,
                MaxOutputTokens = maxOutputTokens,
                Metadata = new Dictionary<string, string>
                {
                    ["node_id"] = currentNode.Id.ToString()
                }
            });
        }

        private static string NormalizeAttachmentMimeType(string? fileName, string? mimeType)
        {
            string ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();

            return ext switch
            {
                // 程式碼 / 純文字
                ".cs" or ".xaml" or ".java" or ".cpp" or ".c" or ".h" or ".hpp"
                    or ".py" or ".js" or ".ts" or ".txt" or ".log" or ".xml"
                    or ".html" or ".htm" or ".css" or ".sh" or ".bat" => "text/plain",
                ".json" => "application/json",
                ".csv"  => "text/csv",
                ".md"   => "text/markdown",
                ".pdf"  => "application/pdf",
                // 圖片
                ".png"  => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".gif"  => "image/gif",
                // Office（提供正確 MIME，各 provider 可自行決定是否接受）
                ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
                ".pptx" => "application/vnd.openxmlformats-officedocument.presentationml.presentation",
                ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
                _ => string.IsNullOrWhiteSpace(mimeType) ? "application/octet-stream" : mimeType
            };
        }

        private string ResolveAbsoluteAttachmentPath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return "";

            string savesDir = @"D:\desk\college\final\file";
            string attachmentsRootDir = Path.Combine(savesDir, "_attachments");

            return Path.Combine(attachmentsRootDir, relativePath);
        }
    }
}