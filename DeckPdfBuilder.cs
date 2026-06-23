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
    /// §7 簡報 PDF：把簡報大綱（與 .pptx 同一份 outline）渲染成「一張投影片一頁」的 PDF，
    /// 版面、分頁與 .pptx 對齊；封面頁嵌入封面圖。讓「簡報 pdf」和 pptx 內容 / 分頁一致。
    /// </summary>
    public static class DeckPdfBuilder
    {
        private const string CjkFont = "Microsoft JhengHei";

        static DeckPdfBuilder()
        {
            QuestPDF.Settings.License = LicenseType.Community;
        }

        public static byte[] Build(PresentationOutlinePayload outline, byte[]? coverImagePng)
        {
            var slides = BuildSlides(outline);

            return Document.Create(doc =>
            {
                doc.Page(page =>
                {
                    // 橫向（接近投影片比例）；一張投影片一頁。
                    page.Size(PageSizes.A4.Landscape());
                    page.Margin(1.5f, Unit.Centimetre);
                    page.DefaultTextStyle(x => x.FontFamily(CjkFont).FontSize(14).LineHeight(1.4f));

                    page.Content().Column(col =>
                    {
                        for (int i = 0; i < slides.Count; i++)
                        {
                            if (i > 0)
                                col.Item().PageBreak();
                            RenderSlide(col, slides[i], i == 0 ? coverImagePng : null);
                        }
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

        private sealed class DeckSlide
        {
            public bool IsCover;
            public string Title = "";
            public string Subtitle = "";
            public string[] Bullets = Array.Empty<string>();
            public byte[]? ImageBytes = null;
        }

        private static List<DeckSlide> BuildSlides(PresentationOutlinePayload outline)
        {
            var list = new List<DeckSlide>();

            if (outline?.Slides == null || outline.Slides.Count == 0)
            {
                list.Add(new DeckSlide
                {
                    IsCover = true,
                    Title = string.IsNullOrWhiteSpace(outline?.Title) ? "簡報" : outline!.Title,
                    Subtitle = outline?.Topic ?? ""
                });
                return list;
            }

            foreach (var s in outline.Slides.OrderBy(x => x.Order))
            {
                bool isCover = string.Equals(s.Kind, "cover", StringComparison.OrdinalIgnoreCase);
                list.Add(new DeckSlide
                {
                    IsCover = isCover,
                    Title = isCover
                        ? (string.IsNullOrWhiteSpace(s.Heading) ? outline.Title : s.Heading)
                        : (s.Heading ?? ""),
                    Subtitle = isCover ? (outline.Topic ?? "") : "",
                    Bullets = (s.Bullets ?? Array.Empty<string>()).ToArray(),
                    ImageBytes = isCover ? null : s.ImageBytes
                });
            }

            return list;
        }

        // 商業主題色（與 PptxBuilder 一致）：深藍、青色強調、淺藍副標。
        private const string Navy = "#1F3864";
        private const string Accent = "#2E9CCA";
        private const string CoverSub = "#C9D6E8";

        private static void RenderSlide(ColumnDescriptor col, DeckSlide slide, byte[]? coverImagePng)
        {
            if (slide.IsCover)
            {
                // 封面：深藍色塊置中標題 + 青色短線 + 副標，下方置中放封面圖。
                col.Item().PaddingTop(24).Background(Navy).Padding(28).Column(c =>
                {
                    c.Item().AlignCenter().Text(slide.Title)
                        .FontSize(30).Bold().FontColor("#FFFFFF");
                    c.Item().PaddingTop(12).AlignCenter().Width(160).LineHorizontal(2).LineColor(Accent);
                    if (!string.IsNullOrWhiteSpace(slide.Subtitle))
                        c.Item().PaddingTop(12).AlignCenter().Text(slide.Subtitle)
                            .FontSize(15).FontColor(CoverSub);
                });

                if (coverImagePng != null && coverImagePng.Length > 0)
                    col.Item().PaddingTop(24).AlignCenter().MaxHeight(300).Image(coverImagePng).FitArea();

                return;
            }

            // 內容頁：深藍標題列（白字）+ 青色線 + 重點（有智慧配圖時左文右圖）。
            col.Item().Background(Navy).PaddingVertical(8).PaddingHorizontal(14)
                .Text(slide.Title).FontSize(20).Bold().FontColor("#FFFFFF");
            col.Item().LineHorizontal(2).LineColor(Accent);
            col.Item().PaddingTop(10);

            bool hasImg = slide.ImageBytes != null && slide.ImageBytes.Length > 0;
            if (hasImg)
            {
                col.Item().Row(row =>
                {
                    row.RelativeItem(6).Column(bc => RenderBullets(bc, slide.Bullets));
                    row.ConstantItem(20);
                    row.RelativeItem(4).AlignMiddle().MaxHeight(300).Image(slide.ImageBytes!).FitArea();
                });
            }
            else
            {
                RenderBullets(col, slide.Bullets);
            }
        }

        private static void RenderBullets(ColumnDescriptor col, string[] bullets)
        {
            foreach (var b in bullets)
            {
                if (string.IsNullOrWhiteSpace(b))
                    continue;

                col.Item().PaddingVertical(3).Row(row =>
                {
                    row.ConstantItem(18).Text("•").FontSize(14).FontColor(Accent);
                    row.RelativeItem().Text(text => RenderInline(text, b));
                });
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
