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
                    MimeType = string.IsNullOrWhiteSpace(a.MimeType) ? "application/octet-stream" : a.MimeType,
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

        private string ResolveAbsoluteAttachmentPath(string? relativePath)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return "";

            string savesDir = @"D:\desk\college\final\cat5201\file";
            string attachmentsRootDir = Path.Combine(savesDir, "_attachments");

            return Path.Combine(attachmentsRootDir, relativePath);
        }
    }
}