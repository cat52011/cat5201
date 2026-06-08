using System;
using System.Text.RegularExpressions;

namespace test
{
    public static class FinalAnswerSanitizer
    {
        private const string HeadingKeyData = "\u95dc\u9375\u8cc7\u6599";
        private const string HeadingTrend = "\u77ed\u671f\u8d70\u52e2\u5224\u65b7";
        private const string HeadingGaps = "\u8cc7\u6599\u885d\u7a81 / \u7f3a\u5931";
        private const string HeadingOneLine = "\u7e3d\u7d50\u4e00\u53e5\u8a71";

        private static readonly Regex CitationMarkerRegex =
            new(@"(?<![A-Za-z0-9])\[(?:\d+|[ivxlcdm]+)(?:\s*[,;]\s*(?:\d+|[ivxlcdm]+))*\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex SourceTagCitationRegex =
            new(@"(?<![A-Za-z0-9])\[(?:[A-Z][A-Z0-9.\-]{1,8})(?:\]\[(?:[A-Z][A-Z0-9.\-]{1,8}))*\]", RegexOptions.Compiled);

        private static readonly Regex EndMarkerRegex =
            new(@"\[\[END_OF_RESPONSE\]\]", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        public static string Sanitize(string text, bool enforceSynthesisFormat)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = EndMarkerRegex.Replace(text, "");
            text = CitationMarkerRegex.Replace(text, "");
            text = SourceTagCitationRegex.Replace(text, "");

            if (enforceSynthesisFormat)
                text = NormalizeLegacyHeadingsOnly(text);

            return CleanupWhitespace(text);
        }

        public static AiFallbackExecutionResult Sanitize(
            AiFallbackExecutionResult result,
            bool enforceSynthesisFormat)
        {
            if (result == null)
            {
                return new AiFallbackExecutionResult
                {
                    IsSuccess = false,
                    ErrorMessage = "Execution result was null."
                };
            }

            return new AiFallbackExecutionResult
            {
                IsSuccess = result.IsSuccess,
                Text = Sanitize(result.Text ?? "", enforceSynthesisFormat),
                ActualModelId = result.ActualModelId ?? "",
                UsedFallback = result.UsedFallback,
                Summary = result.Summary ?? "",
                ErrorMessage = result.ErrorMessage ?? "",
                Attempts = result.Attempts ?? Array.Empty<AiFallbackAttempt>()
            };
        }

        private static string NormalizeLegacyHeadingsOnly(string text)
        {
            text = ReplaceHeading(text, "\u5df2\u77e5\u8cc7\u6599", HeadingKeyData);
            text = ReplaceHeading(text, "\u5408\u7406\u63a8\u8ad6", HeadingTrend);
            text = ReplaceHeading(text, "\u98a8\u96aa / \u4e0d\u78ba\u5b9a\u6027", HeadingGaps);
            text = ReplaceHeading(text, "\u98a8\u96aa\uff0f\u4e0d\u78ba\u5b9a\u6027", HeadingGaps);
            text = ReplaceHeading(text, "\u98a8\u96aa", HeadingGaps);
            text = ReplaceHeading(text, "\u77ed\u671f\u5224\u65b7", HeadingOneLine);
            return text;
        }

        private static string ReplaceHeading(string text, string from, string to)
        {
            string escaped = Regex.Escape(from);
            return Regex.Replace(
                text,
                $@"(?m)^\s*(?:#+\s*)?(?:\*\*)?{escaped}(?:\*\*)?\s*$",
                to);
        }

        private static string CleanupWhitespace(string text)
        {
            text = text.Replace("\r\n", "\n").Replace('\r', '\n');
            text = Regex.Replace(text, @"[ \t]+\n", "\n");
            text = Regex.Replace(text, @"\n{3,}", "\n\n");
            return text.Trim();
        }
    }
}
