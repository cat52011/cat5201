using System;
using System.Collections.Generic;
using System.Linq;
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
                    col.Item().PaddingLeft(12).Text(rawLine)
                        .FontFamily("Consolas").FontSize(10).FontColor(Colors.Grey.Darken3);
                    continue;
                }

                // Markdown 圖片：![alt](本機絕對路徑) → 置中嵌入（報告智慧配圖用）。
                var imgMatch = Regex.Match(rawLine.Trim(), @"^!\[[^\]]*\]\(([^)]+)\)$");
                if (imgMatch.Success)
                {
                    string imgPath = imgMatch.Groups[1].Value.Trim();
                    try
                    {
                        if (System.IO.File.Exists(imgPath))
                        {
                            byte[] imgBytes = System.IO.File.ReadAllBytes(imgPath);
                            col.Item().PaddingVertical(6).AlignCenter().MaxHeight(280).Image(imgBytes).FitArea();
                        }
                    }
                    catch { }
                    continue;
                }

                // Markdown 表格 → QuestPDF 表格。
                if (IsTableRow(rawLine) && i + 1 < lines.Length && IsTableSeparator(lines[i + 1]))
                {
                    var rows = new List<List<string>> { SplitRowCells(rawLine) };
                    int j = i + 2;
                    while (j < lines.Length && IsTableRow(lines[j]))
                    {
                        rows.Add(SplitRowCells(lines[j]));
                        j++;
                    }
                    RenderTable(col, rows);
                    i = j - 1;
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

        private static void RenderTable(ColumnDescriptor col, List<List<string>> rows)
        {
            if (rows.Count == 0)
                return;

            int columnCount = rows.Max(r => r.Count);
            if (columnCount == 0)
                return;

            col.Item().PaddingVertical(4).Table(table =>
            {
                table.ColumnsDefinition(def =>
                {
                    for (int c = 0; c < columnCount; c++)
                        def.RelativeColumn();
                });

                for (int r = 0; r < rows.Count; r++)
                {
                    bool isHeader = r == 0;
                    var cells = rows[r];

                    for (int c = 0; c < columnCount; c++)
                    {
                        string cellText = c < cells.Count ? cells[c] : "";
                        table.Cell()
                            .Border(0.5f).BorderColor("#BFBFBF")
                            .Background(isHeader ? "#F2F2F2" : "#FFFFFF")
                            .Padding(4)
                            .Text(text =>
                            {
                                if (isHeader)
                                    text.Span(cellText).FontSize(10).Bold();
                                else
                                    RenderInline(text, cellText);
                            });
                    }
                }
            });
        }

        private static bool IsTableRow(string line)
        {
            string t = (line ?? "").Trim();
            return t.Length >= 3 && t.StartsWith("|", StringComparison.Ordinal) && t.IndexOf('|', 1) >= 1;
        }

        private static bool IsTableSeparator(string line)
        {
            string t = (line ?? "").Trim();
            if (!t.StartsWith("|", StringComparison.Ordinal))
                return false;

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
