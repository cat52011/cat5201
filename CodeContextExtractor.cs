using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace test
{
    /// <summary>
    /// Code Agent v1.5：針對性 / 分段內容擷取。
    /// 大型檔案不再「只取前段截斷」，改成：檔頭（using/namespace/class）+ 程式結構骨架
    /// + 使用者目標關鍵字命中處的上下文視窗，組成重點摘錄，並回報哪些行被列出 / 略過。
    /// 單次完成，不額外呼叫 LLM，省額度。
    /// </summary>
    public static class CodeContextExtractor
    {
        public sealed class FocusedExcerpt
        {
            public string Text { get; init; } = "";
            public bool Chunked { get; init; }
            public int IncludedLines { get; init; }
            public int TotalLines { get; init; }
            public IReadOnlyList<string> SegmentRanges { get; init; } = new List<string>();
        }

        private static readonly Regex IdentifierRegex = new(
            @"[A-Za-z_][A-Za-z0-9_]{2,}",
            RegexOptions.Compiled);

        private static readonly Regex QuotedRegex = new(
            "\"(?<q>[^\"]{2,40})\"",
            RegexOptions.Compiled);

        private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
        {
            "the", "and", "for", "that", "this", "with", "from", "please",
            "make", "you", "your", "can", "will", "should", "would", "into",
            "then", "code", "file", "fix", "bug", "help", "have", "are", "not",
            "use", "using", "method", "function", "class", "line", "lines"
        };

        public static FocusedExcerpt Build(
            string? content,
            string? userGoal,
            int maxChars,
            int headLines = 40,
            int contextLines = 25)
        {
            content ??= "";

            if (maxChars <= 0)
            {
                return new FocusedExcerpt
                {
                    Text = "",
                    Chunked = content.Trim().Length > 0
                };
            }

            // 小檔：整份送出，不分段。
            if (content.Length <= maxChars)
            {
                int lc = CountLines(content);
                return new FocusedExcerpt
                {
                    Text = content,
                    Chunked = false,
                    IncludedLines = lc,
                    TotalLines = lc,
                    SegmentRanges = new[] { $"1-{Math.Max(1, lc)}" }
                };
            }

            string[] lines = content
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');

            int total = lines.Length;
            var include = new bool[total];

            // 1. 檔頭：using / namespace / class 宣告區。
            for (int i = 0; i < Math.Min(headLines, total); i++)
                include[i] = true;

            // 2. 程式結構骨架：類別 / 方法簽章 / TODO / FIXME / Exception（整檔掃描，單行）。
            for (int i = 0; i < total; i++)
            {
                if (!include[i] && IsStructuralLine(lines[i]))
                    include[i] = true;
            }

            // 3. 目標關鍵字命中處 ± 上下文視窗。
            var anchors = ExtractAnchors(userGoal);
            if (anchors.Count > 0)
            {
                for (int i = 0; i < total; i++)
                {
                    string ln = lines[i];
                    if (string.IsNullOrWhiteSpace(ln))
                        continue;

                    if (anchors.Any(a => ln.Contains(a, StringComparison.OrdinalIgnoreCase)))
                    {
                        int from = Math.Max(0, i - contextLines);
                        int to = Math.Min(total - 1, i + contextLines);
                        for (int j = from; j <= to; j++)
                            include[j] = true;
                    }
                }
            }

            // 4. 在 maxChars 預算內組出摘錄，並標記略過的區段。
            var sb = new StringBuilder();
            var ranges = new List<string>();
            int includedLines = 0;
            int segStart = -1;
            int lastIncluded = -1;
            bool chunked = false;
            bool budgetHit = false;

            for (int i = 0; i < total; i++)
            {
                if (include[i])
                {
                    if (sb.Length + lines[i].Length + 1 > maxChars)
                    {
                        budgetHit = true;
                        break;
                    }

                    if (segStart < 0)
                        segStart = i;

                    sb.Append(lines[i]).Append('\n');
                    lastIncluded = i;
                    includedLines++;
                }
                else
                {
                    if (segStart >= 0)
                    {
                        ranges.Add($"{segStart + 1}-{i}");
                        segStart = -1;
                        chunked = true;
                        sb.Append($"... (略過第 {i + 1} 行起的區段) ...\n");
                    }
                }
            }

            if (segStart >= 0 && lastIncluded >= 0)
                ranges.Add($"{segStart + 1}-{lastIncluded + 1}");

            if (budgetHit || includedLines < total)
                chunked = true;

            return new FocusedExcerpt
            {
                Text = sb.ToString().TrimEnd('\n'),
                Chunked = chunked,
                IncludedLines = includedLines,
                TotalLines = total,
                SegmentRanges = ranges
            };
        }

        private static bool IsStructuralLine(string? raw)
        {
            string line = (raw ?? "").Trim();
            if (line.Length == 0)
                return false;

            if (line.StartsWith("namespace ", StringComparison.Ordinal) ||
                line.StartsWith("using ", StringComparison.Ordinal) ||
                line.StartsWith("package ", StringComparison.Ordinal) ||
                line.StartsWith("import ", StringComparison.Ordinal) ||
                line.StartsWith("class ", StringComparison.Ordinal) ||
                line.Contains(" class ", StringComparison.Ordinal) ||
                line.Contains(" interface ", StringComparison.Ordinal) ||
                line.Contains("TODO", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("FIXME", StringComparison.OrdinalIgnoreCase) ||
                line.Contains("Exception", StringComparison.Ordinal))
            {
                return true;
            }

            return IsLikelyMemberSignature(line);
        }

        private static bool IsLikelyMemberSignature(string line)
        {
            if (!line.Contains('(') || !line.Contains(')'))
                return false;

            if (line.StartsWith("if", StringComparison.Ordinal) ||
                line.StartsWith("for", StringComparison.Ordinal) ||
                line.StartsWith("foreach", StringComparison.Ordinal) ||
                line.StartsWith("while", StringComparison.Ordinal) ||
                line.StartsWith("switch", StringComparison.Ordinal) ||
                line.StartsWith("return", StringComparison.Ordinal) ||
                line.StartsWith("//", StringComparison.Ordinal))
            {
                return false;
            }

            return line.Contains("public ", StringComparison.Ordinal) ||
                   line.Contains("private ", StringComparison.Ordinal) ||
                   line.Contains("protected ", StringComparison.Ordinal) ||
                   line.Contains("internal ", StringComparison.Ordinal) ||
                   line.Contains("static ", StringComparison.Ordinal) ||
                   line.Contains("void ", StringComparison.Ordinal) ||
                   line.Contains("def ", StringComparison.Ordinal);
        }

        private static IReadOnlyList<string> ExtractAnchors(string? userGoal)
        {
            var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            if (string.IsNullOrWhiteSpace(userGoal))
                return set.ToList();

            foreach (Match m in QuotedRegex.Matches(userGoal))
            {
                string q = m.Groups["q"].Value.Trim();
                if (q.Length >= 2)
                    set.Add(q);
            }

            foreach (Match m in IdentifierRegex.Matches(userGoal))
            {
                string token = m.Value;
                if (token.Length < 3 || StopWords.Contains(token))
                    continue;

                set.Add(token);
            }

            // 避免過多 anchor（太發散反而失焦）：保留較長 / 較像識別字的前 12 個。
            return set
                .OrderByDescending(x => x.Length)
                .Take(12)
                .ToList();
        }

        private static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int count = 1;
            foreach (char ch in text)
            {
                if (ch == '\n')
                    count++;
            }

            return count;
        }
    }
}
