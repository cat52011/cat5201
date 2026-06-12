using System;
using System.IO;
using System.Linq;
using System.Text;

namespace test
{
    /// <summary>
    /// File Generation v1：把報告內容安全寫到 _generated 資料夾，回傳 GeneratedFilePayload。
    /// 負責：檔名清理、時間戳避免覆蓋、UTF-8 (含 BOM) 寫入以確保中文在外部編輯器正常顯示。
    /// </summary>
    public static class GeneratedFileWriter
    {
        // UTF-8 with BOM：避免 Windows 記事本等工具把中文判讀成亂碼。
        private static readonly Encoding Utf8Bom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

        public static GeneratedFilePayload WriteMarkdown(
            string outputDir,
            string title,
            string content,
            string sourceSummary,
            string extension = ".md")
        {
            try
            {
                if (string.IsNullOrWhiteSpace(outputDir))
                {
                    return Failed("輸出資料夾未設定。", title);
                }

                Directory.CreateDirectory(outputDir);

                string baseName = SanitizeFileName(title);
                if (string.IsNullOrWhiteSpace(baseName))
                    baseName = "report";

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string fileName = $"{baseName}_{stamp}{extension}";
                string fullPath = Path.GetFullPath(Path.Combine(outputDir, fileName));

                // 確保最終路徑仍在 outputDir 之內（防止清理後殘留的路徑分隔符逃逸）。
                string rootFull = Path.GetFullPath(outputDir);
                if (!fullPath.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    return Failed("輸出路徑超出允許範圍。", title);
                }

                content ??= "";
                File.WriteAllText(fullPath, content, Utf8Bom);

                return new GeneratedFilePayload
                {
                    Format = extension == ".txt" ? "text" : "markdown",
                    FileName = fileName,
                    FilePath = fullPath,
                    Title = title ?? "",
                    CharacterCount = content.Length,
                    ByteCount = Utf8Bom.GetByteCount(content),
                    Success = true,
                    SourceSummary = sourceSummary ?? ""
                };
            }
            catch (Exception ex)
            {
                return Failed(ex.Message, title);
            }
        }

        private static GeneratedFilePayload Failed(string error, string title)
        {
            return new GeneratedFilePayload
            {
                Success = false,
                ErrorMessage = error ?? "未知錯誤",
                Title = title ?? ""
            };
        }

        private static string SanitizeFileName(string name)
        {
            string text = (name ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (string.IsNullOrWhiteSpace(text))
                return "";

            // 移除標題尾端可能加上的省略號，並濾掉檔名非法字元與路徑分隔符。
            text = text.TrimEnd('…', '.', ' ');

            var invalid = Path.GetInvalidFileNameChars()
                .Concat(new[] { '/', '\\' })
                .Distinct()
                .ToArray();

            var sb = new StringBuilder(text.Length);
            foreach (char c in text)
            {
                sb.Append(invalid.Contains(c) ? '_' : c);
            }

            string cleaned = sb.ToString().Trim();

            // 限制長度，避免超長路徑。
            if (cleaned.Length > 50)
                cleaned = cleaned.Substring(0, 50).Trim();

            return cleaned;
        }
    }
}
