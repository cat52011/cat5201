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
            var attachments = _main.GetAttachmentsForNode(currentNode)
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
                ".cs" => "text/plain",
                ".xaml" => "text/plain",
                ".java" => "text/plain",
                ".cpp" => "text/plain",
                ".h" => "text/plain",
                ".hpp" => "text/plain",
                ".py" => "text/plain",
                ".js" => "text/plain",
                ".ts" => "text/plain",
                ".json" => "application/json",
                ".csv" => "text/csv",
                ".txt" => "text/plain",
                ".md" => "text/markdown",
                ".pdf" => "application/pdf",
                _ => string.IsNullOrWhiteSpace(mimeType)
                    ? "application/octet-stream"
                    : mimeType
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