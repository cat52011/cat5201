using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using D = DocumentFormat.OpenXml.Drawing;
using S = DocumentFormat.OpenXml.Spreadsheet;

namespace test
{
    /// <summary>
    /// 從已生成的 Office 檔案抽出純文字，給即時預覽面板顯示用（Phase 1：文字 / 大綱層級）。
    /// 用專案既有的 DocumentFormat.OpenXml，不引入額外渲染依賴。
    /// 高擬真渲染（DOCX 版面、PPTX 縮圖）屬後續階段。
    /// </summary>
    public static class ArtifactTextExtractor
    {
        /// <summary>
        /// DOCX → Markdown（保留字形層級）：依 run 的粗體 + 字級還原成 # / ## / ### 標題、**粗體**、
        /// 斜體中繼列、項目符號與表格，讓預覽看得到字形變化（不再是無格式純文字）。
        /// </summary>
        public static string ExtractDocx(string path)
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null)
                return "";

            var sb = new StringBuilder();
            // 依文件順序逐一處理段落與表格（表格要還原成 Markdown 表格才看得到結構）。
            foreach (var el in body.Elements())
            {
                if (el is W.Paragraph para)
                    sb.AppendLine(ParagraphToMarkdown(para));
                else if (el is W.Table table)
                    AppendTableMarkdown(sb, table);
            }

            return CollapseBlankRuns(sb.ToString());
        }

        private static string ParagraphToMarkdown(W.Paragraph para)
        {
            var runs = para.Elements<W.Run>().ToList();
            if (runs.Count == 0)
                return "";

            int maxSize = 0;
            bool anyBold = false, anyItalic = false;
            foreach (var r in runs)
            {
                var rp = r.RunProperties;
                if (rp?.Bold != null) anyBold = true;
                if (rp?.Italic != null) anyItalic = true;
                if (int.TryParse(rp?.FontSize?.Val?.Value, out int sz) && sz > maxSize)
                    maxSize = sz;
            }

            string text = string.Concat(runs.Select(r => r.InnerText));
            if (string.IsNullOrWhiteSpace(text))
                return "";

            // DocxReportBuilder：標題 = 粗體 + 字級 36/28/24（half-point）。
            if (anyBold && maxSize >= 36) return "# " + text.Trim();
            if (anyBold && maxSize >= 28) return "## " + text.Trim();
            if (anyBold && maxSize >= 24) return "### " + text.Trim();

            // 中繼資料列：斜體小字 → 以斜體呈現。
            if (anyItalic && maxSize > 0 && maxSize <= 18)
                return "*" + text.Trim() + "*";

            // 一般段落：粗體 run 包成 **...**；項目符號「• 」轉成 Markdown「- 」。
            var inline = new StringBuilder();
            foreach (var r in runs)
            {
                string rt = r.InnerText;
                if (string.IsNullOrEmpty(rt)) continue;
                bool bold = r.RunProperties?.Bold != null;
                if (bold && rt.Trim() != "•")
                    inline.Append("**").Append(rt).Append("**");
                else
                    inline.Append(rt);
            }

            string body = inline.ToString();
            string trimmedStart = body.TrimStart();
            if (trimmedStart.StartsWith("• "))
                return "- " + trimmedStart.Substring(2);

            return body;
        }

        private static void AppendTableMarkdown(StringBuilder sb, W.Table table)
        {
            var rows = table.Elements<W.TableRow>().ToList();
            if (rows.Count == 0)
                return;

            for (int r = 0; r < rows.Count; r++)
            {
                var cells = rows[r].Elements<W.TableCell>()
                    .Select(c => c.InnerText.Trim().Replace("|", "\\|"))
                    .ToList();
                if (cells.Count == 0)
                    continue;

                sb.Append("| ").Append(string.Join(" | ", cells)).AppendLine(" |");
                if (r == 0)
                {
                    sb.Append('|');
                    foreach (var _ in cells) sb.Append(" --- |");
                    sb.AppendLine();
                }
            }
            sb.AppendLine();
        }

        /// <summary>PPTX → 逐張投影片的文字行（每張一個清單，第一行通常是標題），供卡片渲染用。</summary>
        public static System.Collections.Generic.List<System.Collections.Generic.List<string>> ExtractPptxSlides(string path)
        {
            var result = new System.Collections.Generic.List<System.Collections.Generic.List<string>>();

            using var pres = PresentationDocument.Open(path, false);
            var presPart = pres.PresentationPart;
            var slideIds = presPart?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>();
            if (presPart == null || slideIds == null)
                return result;

            foreach (var slideId in slideIds)
            {
                var relId = slideId.RelationshipId?.Value;
                if (string.IsNullOrEmpty(relId))
                    continue;

                if (presPart.GetPartById(relId) is not SlidePart slidePart || slidePart.Slide == null)
                    continue;

                var lines = new System.Collections.Generic.List<string>();
                foreach (var t in slidePart.Slide.Descendants<D.Text>())
                {
                    if (!string.IsNullOrWhiteSpace(t.Text))
                        lines.Add(t.Text.Trim());
                }

                result.Add(lines);
            }

            return result;
        }

        /// <summary>PPTX → 第一張含圖投影片的圖片位元組（通常是封面圖），供預覽顯示嵌入的封面圖；無圖回 null。</summary>
        public static byte[]? ExtractPptxFirstImage(string path)
        {
            try
            {
                using var pres = PresentationDocument.Open(path, false);
                var presPart = pres.PresentationPart;
                var slideIds = presPart?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>();
                if (presPart == null || slideIds == null)
                    return null;

                foreach (var slideId in slideIds)
                {
                    var relId = slideId.RelationshipId?.Value;
                    if (string.IsNullOrEmpty(relId))
                        continue;

                    if (presPart.GetPartById(relId) is not SlidePart slidePart)
                        continue;

                    var imagePart = slidePart.ImageParts?.FirstOrDefault();
                    if (imagePart == null)
                        continue;

                    using var s = imagePart.GetStream();
                    using var ms = new System.IO.MemoryStream();
                    s.CopyTo(ms);
                    return ms.ToArray();
                }
                return null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>XLSX → 第一個工作表的逐列儲存格文字（給預覽渲染成 HTML 表格）。</summary>
        public static List<List<string>> ExtractXlsxRows(string path)
        {
            var rows = new List<List<string>>();

            using var doc = SpreadsheetDocument.Open(path, false);
            var wbPart = doc.WorkbookPart;
            var sheet = wbPart?.Workbook?.Sheets?.Elements<S.Sheet>().FirstOrDefault();
            if (wbPart == null || sheet?.Id?.Value == null)
                return rows;

            if (wbPart.GetPartById(sheet.Id.Value) is not WorksheetPart wsPart)
                return rows;

            var sst = wbPart.SharedStringTablePart?.SharedStringTable;
            var sheetData = wsPart.Worksheet?.Elements<S.SheetData>().FirstOrDefault();
            if (sheetData == null)
                return rows;

            foreach (var row in sheetData.Elements<S.Row>())
            {
                var cells = new List<string>();
                foreach (var cell in row.Elements<S.Cell>())
                    cells.Add(GetCellText(cell, sst));
                rows.Add(cells);
            }

            return rows;
        }

        private static string GetCellText(S.Cell cell, S.SharedStringTable? sst)
        {
            if (cell == null)
                return "";

            if (cell.DataType?.Value == S.CellValues.InlineString)
                return cell.InlineString?.Text?.Text ?? cell.InlineString?.InnerText ?? "";

            if (cell.DataType?.Value == S.CellValues.SharedString)
            {
                if (sst != null &&
                    int.TryParse(cell.CellValue?.InnerText, out int idx) &&
                    idx >= 0 && idx < sst.ChildElements.Count)
                {
                    return sst.ChildElements[idx].InnerText;
                }
                return "";
            }

            return cell.CellValue?.InnerText ?? cell.InnerText ?? "";
        }

        /// <summary>PPTX → 逐張投影片的文字大綱，每張以「— 投影片 N —」分隔。</summary>
        public static string ExtractPptx(string path)
        {
            using var pres = PresentationDocument.Open(path, false);
            var presPart = pres.PresentationPart;
            var slideIds = presPart?.Presentation?.SlideIdList?.Elements<DocumentFormat.OpenXml.Presentation.SlideId>();
            if (presPart == null || slideIds == null)
                return "";

            var sb = new StringBuilder();
            int n = 0;
            foreach (var slideId in slideIds)
            {
                n++;
                var relId = slideId.RelationshipId?.Value;
                if (string.IsNullOrEmpty(relId))
                    continue;

                if (presPart.GetPartById(relId) is not SlidePart slidePart || slidePart.Slide == null)
                    continue;

                sb.AppendLine($"—— 投影片 {n} ——");

                foreach (var t in slidePart.Slide.Descendants<D.Text>())
                {
                    if (!string.IsNullOrWhiteSpace(t.Text))
                        sb.AppendLine(t.Text);
                }

                sb.AppendLine();
            }

            return sb.ToString().Trim();
        }

        // 把連續 3 行以上空行壓成一行空行，避免 OpenXml 段落產生過多空白。
        private static string CollapseBlankRuns(string text)
        {
            if (string.IsNullOrEmpty(text))
                return "";

            var lines = text.Replace("\r\n", "\n").Split('\n');
            var sb = new StringBuilder();
            int blankStreak = 0;

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    blankStreak++;
                    if (blankStreak <= 1)
                        sb.AppendLine();
                }
                else
                {
                    blankStreak = 0;
                    sb.AppendLine(line);
                }
            }

            return sb.ToString().Trim();
        }
    }
}
