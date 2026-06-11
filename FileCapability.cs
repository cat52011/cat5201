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
        private const int MaxSnapshotCharsPerFile = 100000;

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
            var snapshots = new List<CodeFileSnapshotItem>();

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
                    string.Equals(ext, ".java", StringComparison.OrdinalIgnoreCase) ||
string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase) ||
string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase) ||
string.Equals(ext, ".cpp", StringComparison.OrdinalIgnoreCase) ||
string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase) ||
string.Equals(ext, ".js", StringComparison.OrdinalIgnoreCase) ||
string.Equals(ext, ".ts", StringComparison.OrdinalIgnoreCase) ||
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
                    FileName = fileName ?? "",
                    Kind = kind ?? "",
                    MimeType = mimeType ?? "",
                    RelativePath = relativePath ?? "",
                    FileType = ResolveFileTypeLabel(ext, mimeType ?? "", isImage),
                    IsImage = isImage,
                    IsPdf = isPdf,
                    IsTextLike = isTextLike,
                    ContentPreview = BuildContentPreview(fileName ?? "", kind ?? "", mimeType ?? "", isImage, isPdf, isTextLike)
                });

                if (isTextLike)
                {
                    var snapshot = TryBuildSnapshot(
                        context.AttachmentsRootDir,
                        relativePath ?? "",
                        fileName ?? "",
                        ResolveFileTypeLabel(ext, mimeType ?? "", isImage));

                    if (snapshot != null)
                        snapshots.Add(snapshot);
                }
            }

            if (items.Count == 0)
                return Task.FromResult(AgentCapabilityResult.NotHandled());

            var payload = new FileSummaryPayload
            {
                Items = items,
                Summary = BuildSummary(items)
            };

            if (snapshots.Count == 0)
            {
                return Task.FromResult(
                    AgentCapabilityResult.WithData("file_summary", payload));
            }

            return Task.FromResult(
                new AgentCapabilityResult
                {
                    Handled = true,
                    Data = new Dictionary<string, object>
                    {
                        ["file_summary"] = payload,
                        ["code_file_snapshot"] = new CodeFileSnapshotPayload
                        {
                            Files = snapshots,
                            Summary = BuildSnapshotSummary(snapshots)
                        }
                    }
                });
        }

        private static string ResolveFileTypeLabel(
            string ext,
            string mimeType,
            bool isImage)
        {
            if (isImage)
                return "image";

            if (string.Equals(ext, ".java", StringComparison.OrdinalIgnoreCase))
                return "java";

            if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase))
                return "csharp";

            if (string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase))
                return "xaml";

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

        private static CodeFileSnapshotItem? TryBuildSnapshot(
            string attachmentsRootDir,
            string relativePath,
            string fileName,
            string fileType)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(attachmentsRootDir) ||
                    string.IsNullOrWhiteSpace(relativePath))
                {
                    return null;
                }

                string root = Path.GetFullPath(attachmentsRootDir);
                string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath))
                {
                    return null;
                }

                string content = File.ReadAllText(fullPath);
                int originalLength = content.Length;
                bool truncated = originalLength > MaxSnapshotCharsPerFile;

                if (truncated)
                    content = content.Substring(0, MaxSnapshotCharsPerFile);

                return new CodeFileSnapshotItem
                {
                    FileName = fileName ?? "",
                    RelativePath = relativePath ?? "",
                    FileType = fileType ?? "",
                    Language = ResolveLanguage(fileName ?? "", fileType ?? ""),
                    CharacterCount = originalLength,
                    LineCount = CountLines(content),
                    IsTruncated = truncated,
                    Content = content
                };
            }
            catch
            {
                return null;
            }
        }

        private static string ResolveLanguage(string fileName, string fileType)
        {
            string ext = Path.GetExtension(fileName ?? "") ?? "";

            if (string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileType, "csharp", StringComparison.OrdinalIgnoreCase))
                return "C#";

            if (string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(fileType, "xaml", StringComparison.OrdinalIgnoreCase))
                return "XAML";

            if (string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase))
                return "Python";

            if (string.Equals(ext, ".js", StringComparison.OrdinalIgnoreCase))
                return "JavaScript";

            if (string.Equals(ext, ".ts", StringComparison.OrdinalIgnoreCase))
                return "TypeScript";

            if (string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase))
                return "JSON";

            if (string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase))
                return "Markdown";

            return fileType ?? "";
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int lines = 1;
            foreach (char c in text)
            {
                if (c == '\n')
                    lines++;
            }

            return lines;
        }

        private static string BuildSnapshotSummary(IReadOnlyList<CodeFileSnapshotItem> snapshots)
        {
            if (snapshots == null || snapshots.Count == 0)
                return "No readable code/text file snapshots.";

            int totalChars = snapshots.Sum(x => x.CharacterCount);
            int truncated = snapshots.Count(x => x.IsTruncated);

            return $"Readable file snapshots: {snapshots.Count}; total chars: {totalChars}; truncated: {truncated}.";
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
