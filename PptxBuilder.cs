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

                foreach (var slide in BuildSlideModels(outline))
                {
                    var slidePart = CreateSlidePart(presentationPart, slideLayoutPart, slide.Title, slide.Body);
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
        }

        private static System.Collections.Generic.IEnumerable<SlideModel> BuildSlideModels(PresentationOutlinePayload outline)
        {
            if (outline?.Slides == null || outline.Slides.Count == 0)
            {
                yield return new SlideModel
                {
                    Title = string.IsNullOrWhiteSpace(outline?.Title) ? "簡報" : outline!.Title,
                    Body = string.IsNullOrWhiteSpace(outline?.Topic) ? Array.Empty<string>() : new[] { outline!.Topic }
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
                        Body = string.IsNullOrWhiteSpace(sub) ? Array.Empty<string>() : new[] { sub }
                    };
                }
                else
                {
                    yield return new SlideModel
                    {
                        Title = s.Heading ?? "",
                        Body = (s.Bullets ?? Array.Empty<string>()).ToArray()
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

        private static SlidePart CreateSlidePart(
            PresentationPart presentationPart,
            SlideLayoutPart slideLayoutPart,
            string title,
            string[] bullets)
        {
            var slidePart = presentationPart.AddNewPart<SlidePart>();

            var shapeTree = new P.ShapeTree(
                new P.NonVisualGroupShapeProperties(
                    new P.NonVisualDrawingProperties { Id = 1U, Name = "" },
                    new P.NonVisualGroupShapeDrawingProperties(),
                    new P.ApplicationNonVisualDrawingProperties()),
                new P.GroupShapeProperties(new A.TransformGroup()));

            // 標題文字框
            shapeTree.Append(MakeTextShape(
                shapeId: 2U,
                name: "Title",
                xEmu: 685800, yEmu: 457200,
                cxEmu: SlideWidth - 1371600, cyEmu: 1143000,
                paragraphs: new[] { new BodyLine(title ?? "", 0) },
                fontSize: 4000, bold: true, colorHex: "1F3864"));

            // 內文 / 重點文字框
            if (bullets != null && bullets.Length > 0)
            {
                var lines = bullets
                    .Where(b => !string.IsNullOrWhiteSpace(b))
                    .Select(b => new BodyLine(b.Trim(), 0))
                    .ToArray();

                if (lines.Length > 0)
                {
                    shapeTree.Append(MakeTextShape(
                        shapeId: 3U,
                        name: "Body",
                        xEmu: 685800, yEmu: 1828800,
                        cxEmu: SlideWidth - 1371600, cyEmu: SlideHeight - 2286000,
                        paragraphs: lines,
                        fontSize: 2000, bold: false, colorHex: "262626", bulleted: true));
                }
            }

            slidePart.Slide = new P.Slide(new P.CommonSlideData(shapeTree), new P.ColorMapOverride(new A.MasterColorMapping()));
            slidePart.AddPart(slideLayoutPart);

            return slidePart;
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
            bool bulleted = false)
        {
            var textBody = new P.TextBody(
                new A.BodyProperties { Wrap = A.TextWrappingValues.Square },
                new A.ListStyle());

            foreach (var line in paragraphs)
            {
                var props = new A.ParagraphProperties { Level = line.Level };
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
