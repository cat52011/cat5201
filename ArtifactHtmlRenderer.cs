using System.Collections.Generic;
using System.Net;
using System.Text;
using Markdig;

namespace test
{
    /// <summary>
    /// 把抽出的文件 / 簡報內容渲染成漂亮的 HTML，給預覽面板的 WebView2 顯示。
    /// 報告：內容本身是 markdown（表格 / 粗體 / 標題），用 Markdig 還原成正確排版。
    /// 簡報：每張投影片渲染成一張卡片。
    /// </summary>
    public static class ArtifactHtmlRenderer
    {
        private static readonly MarkdownPipeline _pipeline =
            new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

        private const string BaseCss = @"
            :root { color-scheme: light; }
            * { box-sizing: border-box; }
            body {
                margin: 0;
                padding: 40px 48px 64px;
                font-family: 'Microsoft JhengHei','Segoe UI',-apple-system,'Helvetica Neue',sans-serif;
                font-size: 15.5px;
                line-height: 1.7;
                color: #24262b;
                background: #fafafa;
            }
            .sheet {
                max-width: 820px;
                margin: 0 auto;
                background: #fff;
                border: 1px solid #ececef;
                border-radius: 14px;
                padding: 44px 52px 52px;
                box-shadow: 0 1px 3px rgba(0,0,0,.04);
            }
            h1,h2,h3,h4 { color: #1b1d22; font-weight: 700; line-height: 1.35; }
            h1 { font-size: 26px; margin: 0 0 18px; }
            h2 { font-size: 20px; margin: 30px 0 12px; padding-bottom: 6px; border-bottom: 2px solid #f0f0f2; }
            h3 { font-size: 17px; margin: 22px 0 8px; }
            p { margin: 10px 0; }
            ul,ol { margin: 10px 0; padding-left: 24px; }
            li { margin: 4px 0; }
            strong { color: #14161a; }
            a { color: #2e6fe0; text-decoration: none; }
            hr { border: none; border-top: 1px solid #ececef; margin: 26px 0; }
            code {
                background: #f3f3f5; border-radius: 5px; padding: 1px 6px;
                font-family: Consolas,'Courier New',monospace; font-size: 0.92em;
            }
            pre { background: #f6f7f9; border-radius: 10px; padding: 14px 16px; overflow-x: auto; }
            pre code { background: none; padding: 0; }
            table {
                border-collapse: collapse; width: 100%; margin: 16px 0; font-size: 14.5px;
            }
            th,td { border: 1px solid #e6e6ea; padding: 9px 13px; text-align: left; vertical-align: top; }
            th { background: #f5f6f8; font-weight: 700; color: #2a2c31; }
            tr:nth-child(even) td { background: #fbfbfc; }
            blockquote {
                margin: 14px 0; padding: 6px 16px; border-left: 3px solid #d6d9e0;
                color: #555; background: #fafafb;
            }";

        private const string SlidesCss = @"
            :root { color-scheme: light; }
            * { box-sizing: border-box; }
            body {
                margin: 0;
                padding: 34px 40px 60px;
                font-family: 'Microsoft JhengHei','Segoe UI',-apple-system,'Helvetica Neue',sans-serif;
                color: #24262b;
                background: #f0f1f4;
            }
            .deck { max-width: 880px; margin: 0 auto; }
            .slide {
                position: relative;
                background: #fff;
                border: 1px solid #e6e6ea;
                border-radius: 14px;
                padding: 30px 36px 32px;
                margin: 0 0 22px;
                box-shadow: 0 2px 10px rgba(0,0,0,.05);
                aspect-ratio: 16 / 9;
                display: flex;
                flex-direction: column;
            }
            .slide-no {
                position: absolute; top: 16px; right: 20px;
                font-size: 12px; color: #b3b6bd; font-weight: 600;
            }
            .slide-title {
                font-size: 23px; font-weight: 700; color: #1b1d22;
                line-height: 1.3; margin: 4px 0 16px; padding-right: 48px;
            }
            .slide-body { font-size: 16px; line-height: 1.6; }
            .slide-body .line { margin: 7px 0; padding-left: 18px; position: relative; color: #3a3d44; }
            .slide-body .line::before {
                content: ''; position: absolute; left: 2px; top: 11px;
                width: 5px; height: 5px; border-radius: 50%; background: #9aa0ab;
            }
            .empty { color: #aeb2ba; font-size: 14px; }";

        /// <summary>報告（markdown 原文）→ 完整 HTML 頁面。</summary>
        public static string BuildDocxHtml(string markdown)
        {
            string body = string.IsNullOrWhiteSpace(markdown)
                ? "<p class='empty'>（沒有可顯示的內容）</p>"
                : Markdown.ToHtml(markdown, _pipeline);

            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.Append("<style>").Append(BaseCss).Append("</style></head><body>");
            sb.Append("<div class='sheet'>").Append(body).Append("</div>");
            sb.Append("</body></html>");
            return sb.ToString();
        }

        /// <summary>簡報（逐張文字行）→ 投影片卡片 HTML。每張 slide 第一行當標題，其餘為內容。</summary>
        public static string BuildSlidesHtml(IReadOnlyList<IReadOnlyList<string>> slides)
        {
            var sb = new StringBuilder();
            sb.Append("<!DOCTYPE html><html><head><meta charset='utf-8'>");
            sb.Append("<style>").Append(SlidesCss).Append("</style></head><body>");
            sb.Append("<div class='deck'>");

            if (slides == null || slides.Count == 0)
            {
                sb.Append("<p class='empty'>（沒有可顯示的投影片）</p>");
            }
            else
            {
                int n = 0;
                foreach (var slide in slides)
                {
                    n++;
                    sb.Append("<div class='slide'>");
                    sb.Append("<div class='slide-no'>").Append(n).Append("</div>");

                    string title = slide != null && slide.Count > 0 ? slide[0] : "";
                    sb.Append("<div class='slide-title'>")
                      .Append(WebUtility.HtmlEncode(title))
                      .Append("</div>");

                    sb.Append("<div class='slide-body'>");
                    if (slide != null)
                    {
                        for (int i = 1; i < slide.Count; i++)
                        {
                            if (string.IsNullOrWhiteSpace(slide[i]))
                                continue;
                            sb.Append("<div class='line'>")
                              .Append(WebUtility.HtmlEncode(slide[i]))
                              .Append("</div>");
                        }
                    }
                    sb.Append("</div></div>");
                }
            }

            sb.Append("</div></body></html>");
            return sb.ToString();
        }
    }
}
