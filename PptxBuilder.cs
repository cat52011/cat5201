using System;
using System.IO;
using System.Linq;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using A = DocumentFormat.OpenXml.Drawing;
using P = DocumentFormat.OpenXml.Presentation;

namespace test
{
    /// <summary>
    /// Presentation 二進位匯出：把 PresentationOutlinePayload 轉成真正的 .pptx（Open XML）。
    /// 為求穩定，每張投影片用手動定位的文字框（標題 + 內文）而非 layout placeholder，
    /// 避免 slide master / layout placeholder 對應的複雜度。產生的檔案可被 PowerPoint / Keynote 開啟。
    /// </summary>
    public static class PptxBuilder
    {
        // 16:9，EMU 單位（1 inch = 914400 EMU）。13.333in x 7.5in。
        private const long SlideWidth = 12192000;
        private const long SlideHeight = 6858000;

        public static byte[] Build(PresentationOutlinePayload outline)
            => Build(outline, null);

        // coverImagePng：非 null 時，封面投影片下半部嵌入該圖（用於「圖片 → 簡報」）。
        public static byte[] Build(PresentationOutlinePayload outline, byte[]? coverImagePng)
        {
            using var stream = new MemoryStream();

            using (var doc = PresentationDocument.Create(stream, PresentationDocumentType.Presentation))
            {
                var presentationPart = doc.AddPresentationPart();
                presentationPart.Presentation = new P.Presentation();

                var (slideMasterPart, slideLayoutPart) = CreateMasterAndLayout(presentationPart);
                CreateThemePart(slideMasterPart);

                var slideIdList = new P.SlideIdList();
                uint slideId = 256;

                var models = BuildSlideModels(outline).ToList();
                string deckTitle = string.IsNullOrWhiteSpace(outline?.Title) ? "" : outline!.Title!.Trim();
                int total = models.Count;

                for (int i = 0; i < models.Count; i++)
                {
                    var slide = models[i];
                    // 封面用 coverImagePng；內容頁用該頁自己的智慧配圖（slide.ImageBytes）。
                    byte[]? imageForSlide = slide.IsCover ? coverImagePng : slide.ImageBytes;

                    // 頁尾：封面不放；其餘顯示「標題 ｜ n / 總數」。
                    string footer = slide.IsCover
                        ? ""
                        : (string.IsNullOrWhiteSpace(deckTitle)
                            ? $"{i + 1} / {total}"
                            : $"{deckTitle}　｜　{i + 1} / {total}");

                    var slidePart = CreateSlidePart(presentationPart, slideLayoutPart, slide, imageForSlide, footer);
                    slideIdList.Append(new P.SlideId
                    {
                        Id = slideId++,
                        RelationshipId = presentationPart.GetIdOfPart(slidePart)
                    });
                }

                presentationPart.Presentation.Append(
                    new P.SlideMasterIdList(
                        new P.SlideMasterId
                        {
                            Id = 2147483648U,
                            RelationshipId = presentationPart.GetIdOfPart(slideMasterPart)
                        }),
                    slideIdList,
                    new P.SlideSize { Cx = (int)SlideWidth, Cy = (int)SlideHeight, Type = P.SlideSizeValues.Screen16x9 },
                    new P.NotesSize { Cx = 6858000, Cy = (int)SlideWidth });

                presentationPart.Presentation.Save();
            }

            return stream.ToArray();
        }

        private sealed class SlideModel
        {
            public string Title = "";
            public string[] Body = Array.Empty<string>();
            public bool IsCover = false;
            public byte[]? ImageBytes = null;
        }

        private static System.Collections.Generic.IEnumerable<SlideModel> BuildSlideModels(PresentationOutlinePayload outline)
        {
            if (outline?.Slides == null || outline.Slides.Count == 0)
            {
                yield return new SlideModel
                {
                    Title = string.IsNullOrWhiteSpace(outline?.Title) ? "簡報" : outline!.Title,
                    Body = string.IsNullOrWhiteSpace(outline?.Topic) ? Array.Empty<string>() : new[] { outline!.Topic },
                    IsCover = true
                };
                yield break;
            }

            foreach (var s in outline.Slides.OrderBy(x => x.Order))
            {
                if (string.Equals(s.Kind, "cover", StringComparison.OrdinalIgnoreCase))
                {
                    string sub = string.IsNullOrWhiteSpace(outline.Topic)
                        ? (s.Bullets != null && s.Bullets.Count > 0 ? s.Bullets[0] : "")
                        : outline.Topic;

                    yield return new SlideModel
                    {
                        Title = string.IsNullOrWhiteSpace(s.Heading) ? outline.Title : s.Heading,
                        Body = string.IsNullOrWhiteSpace(sub) ? Array.Empty<string>() : new[] { sub },
                        IsCover = true
                    };
                }
                else
                {
                    yield return new SlideModel
                    {
                        Title = s.Heading ?? "",
                        Body = (s.Bullets ?? Array.Empty<string>()).ToArray(),
                        ImageBytes = s.ImageBytes
                    };
                }
            }
        }

        private static (SlideMasterPart, SlideLayoutPart) CreateMasterAndLayout(PresentationPart presentationPart)
        {
            var slideMasterPart = presentationPart.AddNewPart<SlideMasterPart>();
            var slideLayoutPart = slideMasterPart.AddNewPart<SlideLayoutPart>();

            slideLayoutPart.SlideLayout = new P.SlideLayout(
                new P.CommonSlideData(new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(new A.TransformGroup()))),
                new P.ColorMapOverride(new A.MasterColorMapping()))
            { Type = P.SlideLayoutValues.Blank };

            slideMasterPart.SlideMaster = new P.SlideMaster(
                new P.CommonSlideData(new P.ShapeTree(
                    new P.NonVisualGroupShapeProperties(
                        new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                        new P.NonVisualGroupShapeDrawingProperties(),
                        new P.ApplicationNonVisualDrawingProperties()),
                    new P.GroupShapeProperties(new A.TransformGroup()))),
                new P.ColorMap
                {
                    Background1 = A.ColorSchemeIndexValues.Light1,
                    Text1 = A.ColorSchemeIndexValues.Dark1,
                    Background2 = A.ColorSchemeIndexValues.Light2,
                    Text2 = A.ColorSchemeIndexValues.Dark2,
                    Accent1 = A.ColorSchemeIndexValues.Accent1,
                    Accent2 = A.ColorSchemeIndexValues.Accent2,
                    Accent3 = A.ColorSchemeIndexValues.Accent3,
                    Accent4 = A.ColorSchemeIndexValues.Accent4,
                    Accent5 = A.ColorSchemeIndexValues.Accent5,
                    Accent6 = A.ColorSchemeIndexValues.Accent6,
                    Hyperlink = A.ColorSchemeIndexValues.Hyperlink,
                    FollowedHyperlink = A.ColorSchemeIndexValues.FollowedHyperlink
                },
                new P.SlideLayoutIdList(
                    new P.SlideLayoutId
                    {
                        Id = 2147483649U,
                        RelationshipId = slideMasterPart.GetIdOfPart(slideLayoutPart)
                    }));

            return (slideMasterPart, slideLayoutPart);
        }

        private static void CreateThemePart(SlideMasterPart slideMasterPart)
        {
            var themePart = slideMasterPart.AddNewPart<ThemePart>();
            themePart.Theme = new A.Theme(
                new A.ThemeElements(
                    new A.ColorScheme(
                        new A.Dark1Color(new A.SystemColor { Val = A.SystemColorValues.WindowText }),
                        new A.Light1Color(new A.SystemColor { Val = A.SystemColorValues.Window }),
                        new A.Dark2Color(new A.RgbColorModelHex { Val = "44546A" }),
                        new A.Light2Color(new A.RgbColorModelHex { Val = "E7E6E6" }),
                        new A.Accent1Color(new A.RgbColorModelHex { Val = "4472C4" }),
                        new A.Accent2Color(new A.RgbColorModelHex { Val = "ED7D31" }),
                        new A.Accent3Color(new A.RgbColorModelHex { Val = "A5A5A5" }),
                        new A.Accent4Color(new A.RgbColorModelHex { Val = "FFC000" }),
                        new A.Accent5Color(new A.RgbColorModelHex { Val = "5B9BD5" }),
                        new A.Accent6Color(new A.RgbColorModelHex { Val = "70AD47" }),
                        new A.Hyperlink(new A.RgbColorModelHex { Val = "0563C1" }),
                        new A.FollowedHyperlinkColor(new A.RgbColorModelHex { Val = "954F72" }))
                    { Name = "Office" },
                    new A.FontScheme(
                        new A.MajorFont(
                            new A.LatinFont { Typeface = "Calibri Light" },
                            new A.EastAsianFont { Typeface = "" },
                            new A.ComplexScriptFont { Typeface = "" }),
                        new A.MinorFont(
                            new A.LatinFont { Typeface = "Calibri" },
                            new A.EastAsianFont { Typeface = "" },
                            new A.ComplexScriptFont { Typeface = "" }))
                    { Name = "Office" },
                    new A.FormatScheme(
                        new A.FillStyleList(
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })),
                        new A.LineStyleList(
                            new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 6350 },
                            new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 12700 },
                            new A.Outline(new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })) { Width = 19050 }),
                        new A.EffectStyleList(
                            new A.EffectStyle(new A.EffectList()),
                            new A.EffectStyle(new A.EffectList()),
                            new A.EffectStyle(new A.EffectList())),
                        new A.BackgroundFillStyleList(
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor }),
                            new A.SolidFill(new A.SchemeColor { Val = A.SchemeColorValues.PhColor })))
                    { Name = "Office" }))
            { Name = "Office Theme" };
        }

        // 商業主題色：深藍標題列 / 色塊、青色強調線、深灰內文、灰頁尾、淺藍副標。
        private const string ThemeNavy = "1F3864";
        private const string ThemeAccent = "2E9CCA";
        private const string ThemeBody = "262626";
        private const string ThemeFooter = "9AA0A6";
        private const string ThemeCoverSub = "C9D6E8";

        private static SlidePart CreateSlidePart(
            PresentationPart presentationPart,
            SlideLayoutPart slideLayoutPart,
            SlideModel slide,
            byte[]? coverImagePng,
            string footerText)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();

            var shapeTree = new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()));

            bool hasImage = coverImagePng != null && coverImagePng.Length > 0;
            string title = slide.Title ?? "";
            string subtitle = slide.Body != null && slide.Body.Length > 0 ? slide.Body[0] : "";

            if (slide.IsCover && hasImage)
            {
                // 封面（含配圖）：頂部深藍標題帶 + 青線，下方白底置中放圖。
                const long bandH = 1645920; // 1.8 in
                shapeTree.Append(MakeRectangle(2U, "CoverBand", 0, 0, SlideWidth, bandH, ThemeNavy));
                shapeTree.Append(MakeRectangle(3U, "CoverAccent", 0, bandH, SlideWidth, 54000, ThemeAccent));
                shapeTree.Append(MakeTextShape(
                    4U, "CoverTitle", 685800, 0, SlideWidth - 1371600, bandH,
                    new[] { new BodyLine(title, 0) },
                    fontSize: 3200, bold: true, colorHex: "FFFFFF", anchorCenter: true));

                if (!string.IsNullOrWhiteSpace(subtitle))
                    shapeTree.Append(MakeTextShape(
                        5U, "CoverSub", 685800, bandH + 182880, SlideWidth - 1371600, 640080,
                        new[] { new BodyLine(subtitle, 0) },
                        fontSize: 1800, bold: false, colorHex: ThemeNavy));

                var imagePart = slidePart.AddImagePart(ImagePartType.Png);
                using (var ms = new MemoryStream(coverImagePng!))
                    imagePart.FeedData(ms);
                string relId = slidePart.GetIdOfPart(imagePart);

                const long side = 3017520;          // ~3.3 in 方形
                long x = (SlideWidth - side) / 2;
                const long y = 2697480;             // 帶 + 副標下方
                shapeTree.Append(MakePicture(6U, "CoverImage", relId, x, y, side, side));
            }
            else if (slide.IsCover)
            {
                // 封面（無圖）：滿版深藍 + 置中白色大標 + 青色短線 + 副標。
                shapeTree.Append(MakeRectangle(2U, "CoverBg", 0, 0, SlideWidth, SlideHeight, ThemeNavy));
                shapeTree.Append(MakeTextShape(
                    3U, "CoverTitle", 914400, 2286000, SlideWidth - 1828800, 1600200,
                    new[] { new BodyLine(title, 0) },
                    fontSize: 4400, bold: true, colorHex: "FFFFFF", alignCenter: true, anchorCenter: true));

                const long lineW = 1828800;
                shapeTree.Append(MakeRectangle(4U, "CoverAccent",
                    (SlideWidth - lineW) / 2, 4023360, lineW, 54000, ThemeAccent));

                if (!string.IsNullOrWhiteSpace(subtitle))
                    shapeTree.Append(MakeTextShape(
                        5U, "CoverSub", 914400, 4206240, SlideWidth - 1828800, 914400,
                        new[] { new BodyLine(subtitle, 0) },
                        fontSize: 2000, bold: false, colorHex: ThemeCoverSub, alignCenter: true));
            }
            else
            {
                // 內容頁：頂部深藍標題列 + 青線 + 內文重點 + 頁尾頁碼。
                const long barH = 1188720; // 1.3 in
                shapeTree.Append(MakeRectangle(2U, "TitleBar", 0, 0, SlideWidth, barH, ThemeNavy));
                shapeTree.Append(MakeRectangle(3U, "TitleAccent", 0, barH, SlideWidth, 54000, ThemeAccent));
                shapeTree.Append(MakeTextShape(
                    4U, "Title", 685800, 0, SlideWidth - 1371600, barH,
                    new[] { new BodyLine(title, 0) },
                    fontSize: 2800, bold: true, colorHex: "FFFFFF", anchorCenter: true));

                // 內容頁有智慧配圖時：內文佔左、圖置於右側（圖文並茂）；無圖時內文佔滿全寬。
                bool contentHasImage = coverImagePng != null && coverImagePng.Length > 0;
                const long imgSide = 3600000;                       // ~3.94 in 方形
                const long imgX = SlideWidth - imgSide - 685800;    // 右側、留右邊距
                const long imgY = 1900000;
                long bodyWidth = contentHasImage
                    ? imgX - 685800 - 274320                        // 左欄寬：到圖左緣前留間距
                    : SlideWidth - 1371600;

                if (slide.Body != null && slide.Body.Length > 0)
                {
                    var lines = slide.Body
                        .Where(b => !string.IsNullOrWhiteSpace(b))
                        .Select(b => new BodyLine(b.Trim(), 0))
                        .ToArray();

                    if (lines.Length > 0)
                        shapeTree.Append(MakeTextShape(
                            5U, "Body", 685800, 1554480, bodyWidth, SlideHeight - 2103120,
                            paragraphs: lines,
                            fontSize: 2000, bold: false, colorHex: ThemeBody, bulleted: true));
                }

                if (contentHasImage)
                {
                    var imagePart = slidePart.AddImagePart(ImagePartType.Png);
                    using (var ms = new MemoryStream(coverImagePng!))
                        imagePart.FeedData(ms);
                    string relId = slidePart.GetIdOfPart(imagePart);
                    shapeTree.Append(MakePicture(7U, "ContentImage", relId, imgX, imgY, imgSide, imgSide));
                }

                if (!string.IsNullOrWhiteSpace(footerText))
                    shapeTree.Append(MakeTextShape(
                        6U, "Footer", 685800, SlideHeight - 457200, SlideWidth - 1371600, 320040,
                        new[] { new BodyLine(footerText, 0) },
                        fontSize: 1100, bold: false, colorHex: ThemeFooter, alignRight: true));
            }

            slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree), new P.ColorMapOverride(new A.MasterColorMapping()));
            slidePart.AddPart(slideLayoutPart);

            return slidePart;
        }

        // 純色矩形（標題列 / 色塊 / 強調線用），無框線、無文字。
        private static P.Shape MakeRectangle(
            uint shapeId, string name, long xEmu, long yEmu, long cxEmu, long cyEmu, string fillHex)
        {
            return new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = shapeId, Name = name },
                    new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = xEmu, Y = yEmu },
                        new A.Extents { Cx = cxEmu, Cy = cyEmu }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle },
                    new A.SolidFill(new A.RgbColorModelHex { Val = fillHex }),
                    new A.Outline(new A.NoFill())),
                new P.TextBody(
                    new A.BodyProperties(),
                    new A.ListStyle(),
                    new A.Paragraph(new A.EndParagraphRunProperties { Language = "zh-TW" })));
        }

        private static P.Picture MakePicture(
            uint shapeId, string name, string relId,
            long xEmu, long yEmu, long cxEmu, long cyEmu)
        {
            return new P.Picture(
                new P.NonVisualPictureProperties(
                    new P.NonVisualDrawingProperties { Id = shapeId, Name = name },
                    new P.NonVisualPictureDrawingProperties(new A.PictureLocks { NoChangeAspect = true }),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.BlipFill(
                    new A.Blip { Embed = relId },
                    new A.Stretch(new A.FillRectangle())),
                new P.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = xEmu, Y = yEmu },
                        new A.Extents { Cx = cxEmu, Cy = cyEmu }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }));
        }

        private readonly struct BodyLine
        {
            public readonly string Text;
            public readonly int Level;
            public BodyLine(string text, int level) { Text = text; Level = level; }
        }

        private static P.Shape MakeTextShape(
            uint shapeId,
            string name,
            long xEmu, long yEmu, long cxEmu, long cyEmu,
            BodyLine[] paragraphs,
            int fontSize,
            bool bold,
            string colorHex,
            bool bulleted = false,
            bool alignCenter = false,
            bool alignRight = false,
            bool anchorCenter = false)
        {
            var bodyProps = new A.BodyProperties { Wrap = A.TextWrappingValues.Square };
            if (anchorCenter)
                bodyProps.Anchor = A.TextAnchoringTypeValues.Center; // 垂直置中（標題列用）

            var textBody = new P.TextBody(bodyProps, new A.ListStyle());

            foreach (var line in paragraphs)
            {
                var props = new A.ParagraphProperties { Level = line.Level };
                if (alignCenter)
                    props.Alignment = A.TextAlignmentTypeValues.Center;
                else if (alignRight)
                    props.Alignment = A.TextAlignmentTypeValues.Right;
                if (!bulleted)
                    props.Append(new A.NoBullet());

                var para = new A.Paragraph(props);
                para.Append(new A.Run(
                    new A.RunProperties(
                        new A.SolidFill(new A.RgbColorModelHex { Val = colorHex }))
                    {
                        Language = "zh-TW",
                        FontSize = fontSize,
                        Bold = bold,
                        Dirty = false
                    },
                    new A.Text(line.Text ?? "")));

                textBody.Append(para);
            }

            return new P.Shape(
                new P.NonVisualShapeProperties(
                    new P.NonVisualDrawingProperties { Id = shapeId, Name = name },
                    new P.NonVisualShapeDrawingProperties(new A.ShapeLocks { NoGrouping = true }),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.ShapeProperties(
                    new A.Transform2D(
                        new A.Offset { X = xEmu, Y = yEmu },
                        new A.Extents { Cx = cxEmu, Cy = cyEmu }),
                    new A.PresetGeometry(new A.AdjustValueList()) { Preset = A.ShapeTypeValues.Rectangle }),
                textBody);
        }
    }
}
