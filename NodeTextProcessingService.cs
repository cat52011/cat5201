using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace test
{
    public sealed class NodeTextProcessingService
    {
        public bool HasEndMarker(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("[[END_OF_RESPONSE]]", StringComparison.Ordinal);
        }

        public string RemoveEndMarker(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            return text.Replace("[[END_OF_RESPONSE]]", "").Trim();
        }

        public string RemoveLeadingOverlap(string existing, string incoming)
        {
            existing ??= "";
            incoming ??= "";

            if (string.IsNullOrWhiteSpace(existing))
                return incoming;

            if (string.IsNullOrWhiteSpace(incoming))
                return "";

            int max = Math.Min(existing.Length, incoming.Length);
            int best = 0;

            for (int len = 1; len <= max; len++)
            {
                var a = existing.Substring(existing.Length - len, len);
                var b = incoming.Substring(0, len);

                if (string.Equals(a, b, StringComparison.Ordinal))
                    best = len;
            }

            return best > 0
                ? incoming.Substring(best)
                : incoming;
        }

        public string RemoveRepeatedBlocks(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var blocks = text
                .Split(new[] { "\r\n\r\n", "\n\n" }, StringSplitOptions.None)
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            var result = new List<string>();

            foreach (var block in blocks)
            {
                bool duplicated = result.Any(existing =>
                    string.Equals(existing, block, StringComparison.Ordinal) ||
                    string.Equals(NormalizeForComparison(existing), NormalizeForComparison(block), StringComparison.Ordinal));

                if (!duplicated)
                    result.Add(block);
            }

            return string.Join(Environment.NewLine + Environment.NewLine, result).Trim();
        }

        public bool IsHighlySimilarByContainment(string a, string b)
        {
            a = NormalizeForComparison(a);
            b = NormalizeForComparison(b);

            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b))
                return false;

            return a.Contains(b, StringComparison.Ordinal) ||
                   b.Contains(a, StringComparison.Ordinal);
        }

        public bool SegmentLooksDuplicate(StringBuilder existingBuilder, string candidate)
        {
            string existing = NormalizeForComparison(existingBuilder?.ToString() ?? "");
            string incoming = NormalizeForComparison(candidate ?? "");

            if (string.IsNullOrWhiteSpace(existing) || string.IsNullOrWhiteSpace(incoming))
                return false;

            if (existing.Contains(incoming, StringComparison.Ordinal))
                return true;

            if (incoming.Length >= 30)
            {
                int partial = Math.Min(incoming.Length, 120);
                string head = incoming.Substring(0, partial);
                if (existing.Contains(head, StringComparison.Ordinal))
                    return true;
            }

            return false;
        }

        private static string NormalizeForComparison(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var sb = new StringBuilder(text.Length);

            foreach (char ch in text)
            {
                if (!char.IsWhiteSpace(ch))
                    sb.Append(char.ToLowerInvariant(ch));
            }

            return sb.ToString();
        }
    }
}