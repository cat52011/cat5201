using System;
using System.Collections.Generic;
using System.IO;
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

            foreach (var rawLine in lines)
            {
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

        private static IEnumerable<Run> ParseInlineRuns(string text)
        {
            // Regex.Split with a capturing group returns alternating: normal, bold, normal, bold...
            // odd-indexed parts are the captured (bold) text.
            var parts = Regex.Split(text, @"\*\*(.+?)\*\*");

            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                    continue;

                var run = new Run();
                if (i % 2 == 1)
                    run.AppendChild(new RunProperties(new Bold()));

                run.AppendChild(new Text(parts[i]) { Space = SpaceProcessingModeValues.Preserve });
                yield return run;
            }
        }
    }
}
