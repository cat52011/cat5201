using System;
using System.Linq;
using System.Text;
using DocumentFormat.OpenXml.Packaging;
using W = DocumentFormat.OpenXml.Wordprocessing;
using D = DocumentFormat.OpenXml.Drawing;

namespace test
{
    /// <summary>
    /// 從已生成的 Office 檔案抽出純文字，給即時預覽面板顯示用（Phase 1：文字 / 大綱層級）。
    /// 用專案既有的 DocumentFormat.OpenXml，不引入額外渲染依賴。
    /// 高擬真渲染（DOCX 版面、PPTX 縮圖）屬後續階段。
    /// </summary>
    public static class ArtifactTextExtractor
    {
        /// <summary>DOCX → 逐段純文字（保留段落換行；表格文字也會被帶到，但不保留表格結構）。</summary>
        public static string ExtractDocx(string path)
        {
            using var doc = WordprocessingDocument.Open(path, false);
            var body = doc.MainDocumentPart?.Document?.Body;
            if (body == null)
                return "";

            var sb = new StringBuilder();
            foreach (var para in body.Descendants<W.Paragraph>())
            {
                // InnerText 會把段落內所有 run 串起來；空段落保留為空行，維持閱讀間距。
                sb.AppendLine(para.InnerText);
            }

            return CollapseBlankRuns(sb.ToString());
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
