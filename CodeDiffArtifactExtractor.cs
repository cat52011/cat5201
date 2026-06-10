using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace test
{
    public static class CodeDiffArtifactExtractor
    {
        private static readonly Regex FencedDiffRegex = new(
            @"```(?:diff|patch)?\s*(?<body>[\s\S]*?)```",
            RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Regex DiffGitRegex = new(
            @"^diff --git a/(?<old>.+?) b/(?<new>.+?)\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public static CodeDiffArtifactPayload? TryExtractReadyDiff(
            string? output,
            string? userGoal)
        {
            if (string.IsNullOrWhiteSpace(output))
                return null;

            string diffText = ExtractDiffText(output);
            if (string.IsNullOrWhiteSpace(diffText))
                return null;

            if (diffText.Contains("@@ draft @@", StringComparison.OrdinalIgnoreCase))
                return null;

            var files = BuildFileChanges(diffText);
            if (files.Count == 0)
                return null;

            int added = files.Sum(x => x.AddedLines);
            int removed = files.Sum(x => x.RemovedLines);

            if (added == 0 && removed == 0)
                return null;

            return new CodeDiffArtifactPayload
            {
                Title = $"Code Diff - {Trim(userGoal, 64)}",
                Status = "ready",
                BaseLabel = "attached snapshot",
                TargetLabel = "model proposed patch",
                Files = files,
                UnifiedDiff = diffText.Trim(),
                Notes = new[]
                {
                    "This diff was extracted from the model output.",
                    "It has not been applied to any file.",
                    "A sandbox/apply step should validate it before changing files."
                }
            };
        }

        private static string ExtractDiffText(string output)
        {
            foreach (Match match in FencedDiffRegex.Matches(output))
            {
                string body = match.Groups["body"].Value.Trim();
                if (LooksLikeUnifiedDiff(body))
                    return body;
            }

            int start = output.IndexOf("diff --git ", StringComparison.OrdinalIgnoreCase);
            if (start >= 0)
                return output.Substring(start).Trim();

            return "";
        }

        private static bool LooksLikeUnifiedDiff(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("diff --git ", StringComparison.OrdinalIgnoreCase) ||
                   (text.Contains("--- ", StringComparison.Ordinal) &&
                    text.Contains("+++ ", StringComparison.Ordinal) &&
                    text.Contains("@@", StringComparison.Ordinal));
        }

        private static IReadOnlyList<CodeDiffFileChange> BuildFileChanges(string diffText)
        {
            var matches = DiffGitRegex.Matches(diffText);
            if (matches.Count == 0)
            {
                var single = BuildSingleFileChange(diffText);
                return single == null
                    ? Array.Empty<CodeDiffFileChange>()
                    : new[] { single };
            }

            var files = new List<CodeDiffFileChange>();

            for (int i = 0; i < matches.Count; i++)
            {
                var current = matches[i];
                int start = current.Index;
                int end = i + 1 < matches.Count
                    ? matches[i + 1].Index
                    : diffText.Length;

                string block = diffText.Substring(start, end - start);
                string path = current.Groups["new"].Value.Trim();

                var counts = CountChangedLines(block);
                files.Add(new CodeDiffFileChange
                {
                    Path = path,
                    ChangeType = ResolveChangeType(block),
                    AddedLines = counts.Added,
                    RemovedLines = counts.Removed,
                    Summary = BuildSummary(counts.Added, counts.Removed)
                });
            }

            return files;
        }

        private static CodeDiffFileChange? BuildSingleFileChange(string diffText)
        {
            string path = ExtractSinglePath(diffText);
            if (string.IsNullOrWhiteSpace(path))
                return null;

            var counts = CountChangedLines(diffText);
            return new CodeDiffFileChange
            {
                Path = path,
                ChangeType = ResolveChangeType(diffText),
                AddedLines = counts.Added,
                RemovedLines = counts.Removed,
                Summary = BuildSummary(counts.Added, counts.Removed)
            };
        }

        private static string ExtractSinglePath(string diffText)
        {
            foreach (string raw in SplitLines(diffText))
            {
                string line = raw.Trim();
                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    string path = line.Substring(4).Trim();
                    if (path.StartsWith("b/", StringComparison.OrdinalIgnoreCase))
                        path = path.Substring(2);

                    if (!string.Equals(path, "/dev/null", StringComparison.OrdinalIgnoreCase))
                        return path;
                }
            }

            return "";
        }

        private static (int Added, int Removed) CountChangedLines(string diffBlock)
        {
            int added = 0;
            int removed = 0;

            foreach (string line in SplitLines(diffBlock))
            {
                if (line.StartsWith("+++", StringComparison.Ordinal) ||
                    line.StartsWith("---", StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.StartsWith("+", StringComparison.Ordinal))
                    added++;
                else if (line.StartsWith("-", StringComparison.Ordinal))
                    removed++;
            }

            return (added, removed);
        }

        private static string ResolveChangeType(string diffBlock)
        {
            if (diffBlock.Contains("new file mode", StringComparison.OrdinalIgnoreCase) ||
                diffBlock.Contains("--- /dev/null", StringComparison.OrdinalIgnoreCase))
            {
                return "add";
            }

            if (diffBlock.Contains("deleted file mode", StringComparison.OrdinalIgnoreCase) ||
                diffBlock.Contains("+++ /dev/null", StringComparison.OrdinalIgnoreCase))
            {
                return "delete";
            }

            if (diffBlock.Contains("rename from ", StringComparison.OrdinalIgnoreCase) ||
                diffBlock.Contains("rename to ", StringComparison.OrdinalIgnoreCase))
            {
                return "rename";
            }

            return "modify";
        }

        private static string BuildSummary(int added, int removed)
            => $"Model proposed unified diff with +{added}/-{removed} line changes.";

        private static IEnumerable<string> SplitLines(string text)
            => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static string Trim(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "requested code change";

            string trimmed = text.Trim();
            return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max) + "...";
        }
    }
}
