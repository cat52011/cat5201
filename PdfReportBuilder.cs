using System;
using System.Text.RegularExpressions;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace test
{
    /// <summary>
    /// File Generation v1 PDF：把 MarkdownReportBuilder 產生的 Markdown 字串轉成 .pdf 二進位（QuestPDF）。
    /// 支援：H1/H2/H3 標題、blockquote 中繼資料、清單（-/*）、**粗體**、程式碼區塊、一般段落。
    /// 使用 Microsoft JhengHei 確保繁體中文正常顯示。
    /// </summary>
    public static class PdfReportBuilder
    {
        // QuestPDF 社群授權：年營收 < $1M 免費（專題 / MVP 階段適用）。必須在產生前設定一次。
        private const string CjkFont = "Microsoft JhengHei";

        static PdfReportBuilder()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public static byte[] Build(string markdownContent)
        {
            string markdown = (markdownContent ?? "").Replace("\r\n", "\n").Replace('\r', '\n');
            var lines = markdown.Split('\n');

            return Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    page.Size(PageSizes.A4);
                    page.Margin(2, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontFamily(CjkFont).FontSize(11).LineHeight(1.4f));

                    page.Content().Column(col =>
                    {
                        col.Spacing(4);
                        RenderLines(col, lines);
                    });

                    page.Footer().AlignCenter().Text(text =>
                    {
                        text.DefaultTextStyle(x => x.FontFamily(CjkFont).FontSize(9).FontColor(Colors.Grey.Medium));
                        text.CurrentPageNumber();
                        text.Span(" / ");
                        text.TotalPages();
                    });
                });
            }).GeneratePdf();
        }

        private static void RenderLines(ColumnDescriptor col, string[] lines)
        {
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
                    col.Item().PaddingLeft(12).Text(rawLine)
                        .FontFamily("Consolas").FontSize(10).FontColor(Colors.Grey.Darken3);
                    continue;
                }

                if (rawLine.StartsWith("# "))
                {
                    col.Item().PaddingTop(8).Text(rawLine.Substring(2).Trim())
                        .FontSize(20).Bold().FontColor("#1F3864");
                }
                else if (rawLine.StartsWith("## "))
                {
                    col.Item().PaddingTop(6).Text(rawLine.Substring(3).Trim())
                        .FontSize(15).Bold().FontColor("#1F3864");
                }
                else if (rawLine.StartsWith("### "))
                {
                    col.Item().PaddingTop(4).Text(rawLine.Substring(4).Trim())
                        .FontSize(13).Bold().FontColor("#2E4D7B");
                }
                else if (rawLine.StartsWith("> "))
                {
                    col.Item().PaddingLeft(8).Text(rawLine.Substring(2).Trim())
                        .Italic().FontSize(10).FontColor(Colors.Grey.Darken1);
                }
                else if (Regex.IsMatch(rawLine, @"^[-*]\s+"))
                {
                    string body = Regex.Replace(rawLine, @"^[-*]\s+", "");
                    col.Item().PaddingLeft(10).Row(row =>
                    {
                        row.ConstantItem(12).Text("•");
                        row.RelativeItem().Text(text => RenderInline(text, body));
                    });
                }
                else if (string.IsNullOrWhiteSpace(rawLine))
                {
                    // 空行：靠 Column.Spacing 控制間距，這裡不額外加。
                }
                else if (rawLine.Trim().Length > 0 && rawLine.Trim().Replace("-", "").Length == 0)
                {
                    // 水平線 --- → 用一條淡灰分隔線取代。
                    col.Item().PaddingVertical(4).LineHorizontal(0.5f).LineColor(Colors.Grey.Lighten1);
                }
                else
                {
                    col.Item().Text(text => RenderInline(text, rawLine));
                }
            }
        }

        // 把一行裡的 **粗體** 拆成 spans。
        private static void RenderInline(TextDescriptor text, string line)
        {
            var parts = Regex.Split(line ?? "", @"\*\*(.+?)\*\*");
            for (int i = 0; i < parts.Length; i++)
            {
                if (string.IsNullOrEmpty(parts[i]))
                    continue;

                if (i % 2 == 1)
                    text.Span(parts[i]).Bold();
                else
                    text.Span(parts[i]);
            }
        }
    }
}
