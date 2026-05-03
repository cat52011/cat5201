using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class FileCapability : IAgentCapability
    {
        public string Id => "file-capability";

        public AgentCapability RequiredAgentCapability => AgentCapability.FileTool;

        public bool CanHandle(AgentExecutionContext context)
        {
            if (context == null || context.Attachments == null)
                return false;

            return context.Attachments.Any();
        }

        public Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct)
        {
            var attachments = context.Attachments ?? Array.Empty<MainWindow.AttachmentInfo>();
            if (attachments.Count == 0)
                return Task.FromResult(AgentCapabilityResult.NotHandled());

            var items = new List<FileSummaryItem>();

            foreach (var a in attachments)
            {
                if (a == null)
                    continue;

                string fileName = a.FileName ?? "";
                string kind = a.Kind ?? "";
                string mimeType = a.MimeType ?? "";
                string relativePath = a.RelativePath ?? "";
                string ext = Path.GetExtension(fileName ?? "") ?? "";

                bool isImage =
                    string.Equals(kind, "image", StringComparison.OrdinalIgnoreCase);

                bool isPdf =
                    string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase);

                bool isTextLike =
                    string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mimeType, "text/markdown", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mimeType, "text/csv", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(mimeType, "application/json", StringComparison.OrdinalIgnoreCase);

                items.Add(new FileSummaryItem
                {
                    FileName = fileName,
                    Kind = kind,
                    MimeType = mimeType,
                    RelativePath = relativePath,
                    FileType = ResolveFileTypeLabel(ext, mimeType, isImage),
                    IsImage = isImage,
                    IsPdf = isPdf,
                    IsTextLike = isTextLike,
                    ContentPreview = BuildContentPreview(fileName, kind, mimeType, isImage, isPdf, isTextLike)
                });
            }

            if (items.Count == 0)
                return Task.FromResult(AgentCapabilityResult.NotHandled());

            var payload = new FileSummaryPayload
            {
                Items = items,
                Summary = BuildSummary(items)
            };

            return Task.FromResult(
                AgentCapabilityResult.WithData("file_summary", payload));
        }

        private static string ResolveFileTypeLabel(
            string ext,
            string mimeType,
            bool isImage)
        {
            if (isImage)
                return "image";

            if (string.Equals(ext, ".pdf", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mimeType, "application/pdf", StringComparison.OrdinalIgnoreCase))
                return "pdf";

            if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mimeType, "application/json", StringComparison.OrdinalIgnoreCase))
                return "json";

            if (string.Equals(ext, ".csv", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mimeType, "text/csv", StringComparison.OrdinalIgnoreCase))
                return "csv";

            if (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mimeType, "text/markdown", StringComparison.OrdinalIgnoreCase))
                return "markdown";

            if (string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase))
                return "text";

            return "file";
        }

        private static string BuildContentPreview(
            string fileName,
            string kind,
            string mimeType,
            bool isImage,
            bool isPdf,
            bool isTextLike)
        {
            if (isImage)
                return "此附件為圖片，回答時應優先依據圖片內容。";

            if (isPdf)
                return "此附件為 PDF，可能包含多段文件內容，適合摘要、翻譯、擷取。";

            if (isTextLike)
                return "此附件為可讀文字檔，適合進行摘要、翻譯、欄位抽取或程式分析。";

            if (!string.IsNullOrWhiteSpace(mimeType))
                return $"此附件 MIME 類型為 {mimeType}。";

            if (!string.IsNullOrWhiteSpace(kind))
                return $"此附件類型為 {kind}。";

            return $"附件：{fileName}";
        }

        private static string BuildSummary(IReadOnlyList<FileSummaryItem> items)
        {
            if (items == null || items.Count == 0)
                return "本次無可用附件。";

            int imageCount = items.Count(x => x.IsImage);
            int pdfCount = items.Count(x => x.IsPdf);
            int textLikeCount = items.Count(x => x.IsTextLike);

            var parts = new List<string>
            {
                $"本次任務共附加 {items.Count} 個附件。"
            };

            if (imageCount > 0)
                parts.Add($"圖片 {imageCount} 個");

            if (pdfCount > 0)
                parts.Add($"PDF {pdfCount} 個");

            if (textLikeCount > 0)
                parts.Add($"可讀文字檔 {textLikeCount} 個");

            parts.Add("附件內容屬高優先來源，回答時應優先依據附件，而不是憑空補充。");

            return string.Join(" ", parts);
        }
    }
}