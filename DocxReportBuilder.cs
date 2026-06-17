using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Wordprocessing;

namespace test
{
    /// <summary>
    /// File Generation v1 DOCX：把 MarkdownReportBuilder 產生的 Markdown 字串
    /// 轉成 .docx 二進位（Word Open XML），不需要額外 LLM 或樣板檔。
    /// 支援：H1/H2/H3 標題、blockquote 中繼資料、清單（-/*）、編號清單、**粗體**、一般段落。
    /// </summary>
    public static class DocxReportBuilder
    {
        public static byte[] Build(string markdownContent)
        {
            using var stream = new MemoryStream();

            using (var doc = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document))
            {
                var mainPart = doc.AddMainDocumentPart();
                var body = new Body();

                AppendParagraphs(body, markdownContent ?? "");
                body.AppendChild(new SectionProperties());

                mainPart.Document = new Document(body);
                mainPart.Document.Save();
            }

            return stream.ToArray();
        }

        private static void AppendParagraphs(Body body, string markdown)
        {
            var lines = markdown.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            bool inFence = false;

            for (int i = 0; i < lines.Length; i++)
            {
                var rawLine = lines[i];

                if (rawLine.TrimStart().StartsWith("```"))
                {
                    inFence = !inFence;
                    continue;
                }

                if (inFence)
                {
                    body.AppendChild(MakeCodeParagraph(rawLine));
                    continue;
                }

                // Markdown 表格：本行與下一行分別是「表頭列」「分隔列(---)」，整塊轉成真正的 Word 表格。
                if (IsTableRow(rawLine) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    var tableLines = new List<string> { rawLine };
                    int j = i + 2; // 跳過分隔列
                    while (j < lines.Length && IsTableRow(lines[j]))
                    {
                        tableLines.Add(lines[j]);
                        j++;
                    }

                    body.AppendChild(MakeTable(tableLines));
                    // 表格後補一個小間距段落，避免和後續內容黏在一起。
                    body.AppendChild(new Paragraph(new ParagraphProperties(
                        new SpacingBetweenLines { Before = "0", After = "120" })));

                    i = j - 1;
                    continue;
                }

                if (rawLine.StartsWith("# "))
                    body.AppendChild(MakeHeadingParagraph(rawLine.Substring(2).Trim(), 36, "480", "200"));
                else if (rawLine.StartsWith("## "))
                    body.AppendChild(MakeHeadingParagraph(rawLine.Substring(3).Trim(), 28, "320", "160"));
                else if (rawLine.StartsWith("### "))
                    body.AppendChild(MakeHeadingParagraph(rawLine.Substring(4).Trim(), 24, "200", "120"));
                else if (rawLine.StartsWith("> "))
                    body.AppendChild(MakeMetaParagraph(rawLine.Substring(2).Trim()));
                else if (Regex.IsMatch(rawLine, @"^[-*]\s+"))
                    body.AppendChild(MakeListParagraph(Regex.Replace(rawLine, @"^[-*]\s+", "")));
                else if (Regex.IsMatch(rawLine, @"^\d+\.\s+"))
                    body.AppendChild(MakeBodyParagraph(rawLine));
                else if (rawLine.Trim().Length > 0 && rawLine.Trim().Replace("-", "").Length == 0)
                { /* horizontal rule — skip */ }
                else if (string.IsNullOrWhiteSpace(rawLine))
                    body.AppendChild(new Paragraph(new ParagraphProperties(
                        new SpacingBetweenLines { Before = "0", After = "120" })));
                else
                    body.AppendChild(MakeBodyParagraph(rawLine));
            }
        }

        private static Paragraph MakeHeadingParagraph(
            string text, int halfPtSize, string spaceBefore, string spaceAfter)
        {
            var para = new Paragraph();
            para.AppendChild(new ParagraphProperties(
                new SpacingBetweenLines { Before = spaceBefore, After = spaceAfter }));

            var run = new Run();
            run.AppendChild(new RunProperties(
                new Bold(),
                new FontSize { Val = new StringValue(halfPtSize.ToString()) }));
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            para.AppendChild(run);
            return para;
        }

        private static Paragraph MakeMetaParagraph(string text)
        {
            var para = new Paragraph();
            para.AppendChild(new ParagraphProperties(
                new Indentation { Left = "360" },
                new SpacingBetweenLines { Before = "0", After = "160" }));

            var run = new Run();
            run.AppendChild(new RunProperties(
                new Italic(),
                new Color { Val = "666666" },
                new FontSize { Val = "18" }));
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            para.AppendChild(run);
            return para;
        }

        private static Paragraph MakeListParagraph(string text)
        {
            var para = new Paragraph();
            para.AppendChild(new ParagraphProperties(
                new Indentation { Left = "360", Hanging = "240" },
                new SpacingBetweenLines { Before = "0", After = "80" }));

            var bulletRun = new Run();
            bulletRun.AppendChild(new Text("• ") { Space = SpaceProcessingModeValues.Preserve });
            para.AppendChild(bulletRun);

            foreach (var run in ParseInlineRuns(text))
                para.AppendChild(run);

            return para;
        }

        private static Paragraph MakeBodyParagraph(string text)
        {
            var para = new Paragraph();
            para.AppendChild(new ParagraphProperties(
                new SpacingBetweenLines { Before = "0", After = "120" }));

            foreach (var run in ParseInlineRuns(text))
                para.AppendChild(run);

            return para;
        }

        private static Paragraph MakeCodeParagraph(string text)
        {
            var para = new Paragraph();
            para.AppendChild(new ParagraphProperties(
                new Indentation { Left = "720" }));

            var run = new Run();
            run.AppendChild(new RunProperties(
                new RunFonts { Ascii = "Courier New", HighAnsi = "Courier New" },
                new FontSize { Val = "20" },
                new Color { Val = "444444" }));
            run.AppendChild(new Text(text) { Space = SpaceProcessingModeValues.Preserve });
            para.AppendChild(run);
            return para;
        }

        // ---- Markdown 表格 → Word 表格 ----

        private static bool IsTableRow(string line)
        {
            string t = (line ?? "").Trim();
            // 至少要有一個內部分隔（| a | b |），單純 "|" 或空字串不算。
            return t.Length >= 3 && t.StartsWith("|", StringComparison.Ordinal) && t.Contains('|', StringComparison.Ordinal)
                   && t.IndexOf('|', 1) >= 1;
        }

        private static bool IsTableSeparator(string line)
        {
            string t = (line ?? "").Trim();
            if (!t.StartsWith("|", StringComparison.Ordinal))
                return false;

            // 每個 cell 只由 - : 空白組成，且至少含一個 '-'。
            bool sawDash = false;
            foreach (char c in t)
            {
                if (c == '-') sawDash = true;
                else if (c != '|' && c != ':' && c != ' ' && c != '\t')
                    return false;
            }
            return sawDash;
        }

        private static List<string> SplitRowCells(string line)
        {
            string t = (line ?? "").Trim();
            if (t.StartsWith("|", StringComparison.Ordinal))
                t = t.Substring(1);
            if (t.EndsWith("|", StringComparison.Ordinal))
                t = t.Substring(0, t.Length - 1);

            return t.Split('|').Select(c => c.Trim()).ToList();
        }

        private static Table MakeTable(List<string> tableLines)
        {
            var props = new TableProperties(
                new TableBorders(
                    new TopBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new BottomBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new LeftBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new RightBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new InsideHorizontalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" },
                    new InsideVerticalBorder { Val = BorderValues.Single, Size = 4, Color = "BFBFBF" }),
                new TableWidth { Width = "5000", Type = TableWidthUnitValues.Pct });

            var table = new Table(props);

            for (int r = 0; r < tableLines.Count; r++)
            {
                bool isHeader = r == 0;
                var cells = SplitRowCells(tableLines[r]);
                var row = new TableRow();

                foreach (var cellText in cells)
                {
                    var cellProps = new TableCellProperties(
                        new TableCellWidth { Type = TableWidthUnitValues.Auto });

                    if (isHeader)
                        cellProps.Append(new Shading
                        {
                            Val = ShadingPatternValues.Clear,
                            Color = "auto",
                            Fill = "F2F2F2"
                        });

                    var para = new Paragraph(new ParagraphProperties(
                        new SpacingBetweenLines { Before = "20", After = "20" }));

                    foreach (var run in ParseInlineRuns(cellText, forceBold: isHeader))
                        para.AppendChild(run);

                    row.Append(new TableCell(cellProps, para));
                }

                table.Append(row);
            }

            return table;
        }

        private static IEnumerable<Run> ParseInlineRuns(string text)
            => ParseInlineRuns(text, forceBold: false);

        private static IEnumerable<Run> ParseInlineRuns(string text, bool forceBold)
        {
            // Regex.Split with a capturing group returns alternating: normal, bold, normal, bold...
            // odd-indexed parts are the captured (bold) text.
            var parts = Regex.Split(text, @"\*\*(.+?)\*\*");

            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                    continue;

                var run = new Run();
                if (i % 2 == 1 || forceBold)
                    run.AppendChild(new RunProperties(new Bold()));

                run.AppendChild(new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
                yield return run;
            }
        }
    }
}
