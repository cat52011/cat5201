using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace test
{
    public static class CodeDiffDryRunValidator
    {
        private static readonly Regex DiffGitRegex = new(
            @"^diff --git a/(?<old>.+?) b/(?<new>.+?)\s*$",
            RegexOptions.Multiline | RegexOptions.Compiled);

        public static CodeDiffValidationPayload Validate(
            CodeDiffArtifactPayload? diff,
            CodeFileSnapshotPayload? snapshot)
        {
            if (diff == null)
            {
                return Invalid("No code diff artifact is available.");
            }

            if (snapshot == null || snapshot.Files == null || snapshot.Files.Count == 0)
            {
                return Invalid("No attachment snapshot is available for dry-run validation.");
            }

            if (string.IsNullOrWhiteSpace(diff.UnifiedDiff))
            {
                return Invalid("Diff artifact does not contain unified diff text.");
            }

            var fileResults = new List<CodeDiffValidationFileResult>();
            var messages = new List<string>();

            foreach (var block in SplitFileBlocks(diff.UnifiedDiff))
            {
                string path = ExtractPath(block);
                var target = FindSnapshot(snapshot, path);
                var counts = CountChangedLines(block);

                if (target == null)
                {
                    fileResults.Add(new CodeDiffValidationFileResult
                    {
                        Path = path,
                        Status = "invalid",
                        AddedLines = counts.Added,
                        RemovedLines = counts.Removed,
                        Message = "Diff target file was not found in attachment snapshot."
                    });
                    continue;
                }

                if (target.IsTruncated)
                {
                    fileResults.Add(new CodeDiffValidationFileResult
                    {
                        Path = path,
                        Status = "warning",
                        AddedLines = counts.Added,
                        RemovedLines = counts.Removed,
                        Message = "Snapshot is truncated, so validation is partial."
                    });
                    continue;
                }

                var validation = ValidateBlockAgainstContent(block, target.Content ?? "");

                fileResults.Add(new CodeDiffValidationFileResult
                {
                    Path = path,
                    Status = validation.Status,
                    AddedLines = counts.Added,
                    RemovedLines = counts.Removed,
                    Message = validation.Message
                });
            }

            if (fileResults.Count == 0)
                return Invalid("No diff file block could be parsed.");

            bool hasInvalid = fileResults.Any(x => string.Equals(x.Status, "invalid", StringComparison.OrdinalIgnoreCase));
            bool hasWarning = fileResults.Any(x => string.Equals(x.Status, "warning", StringComparison.OrdinalIgnoreCase));

            string status = hasInvalid ? "invalid" : hasWarning ? "warning" : "valid";

            messages.Add(status switch
            {
                "valid" => "Dry-run validation found matching context/removal lines in attachment snapshots.",
                "warning" => "Dry-run validation completed with warnings.",
                _ => "Dry-run validation found problems. Do not apply this diff without review."
            });

            return new CodeDiffValidationPayload
            {
                Status = status,
                Summary = $"Dry-run validation: {status}; files={fileResults.Count}.",
                Files = fileResults,
                Messages = messages
            };
        }

        private static CodeDiffValidationPayload Invalid(string message)
        {
            return new CodeDiffValidationPayload
            {
                Status = "invalid",
                Summary = message,
                Messages = new[] { message }
            };
        }

        private static IReadOnlyList<string> SplitFileBlocks(string unifiedDiff)
        {
            unifiedDiff ??= "";

            var matches = DiffGitRegex.Matches(unifiedDiff);
            if (matches.Count == 0)
                return string.IsNullOrWhiteSpace(unifiedDiff) ? Array.Empty<string>() : new[] { unifiedDiff };

            var result = new List<string>();

            for (int i = 0; i < matches.Count; i++)
            {
                int start = matches[i].Index;
                int end = i + 1 < matches.Count ? matches[i + 1].Index : unifiedDiff.Length;
                result.Add(unifiedDiff.Substring(start, end - start));
            }

            return result;
        }

        private static string ExtractPath(string diffBlock)
        {
            diffBlock ??= "";

            var match = DiffGitRegex.Match(diffBlock ?? "");
            if (match.Success)
                return match.Groups["new"].Value.Trim();

            foreach (string raw in SplitLines(diffBlock))
            {
                string line = raw.Trim();
                if (line.StartsWith("+++ ", StringComparison.Ordinal))
                {
                    string path = line.Substring(4).Trim();
                    return path.StartsWith("b/", StringComparison.OrdinalIgnoreCase)
                        ? path.Substring(2)
                        : path;
                }
            }

            return "";
        }

        private static CodeFileSnapshotItem? FindSnapshot(
            CodeFileSnapshotPayload snapshot,
            string path)
        {
            string normalized = NormalizePath(path);
            string fileName = normalized.Split('/').LastOrDefault() ?? normalized;

            return snapshot.Files?
                .FirstOrDefault(x =>
                    string.Equals(NormalizePath(x.RelativePath), normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizePath(x.FileName), normalized, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(NormalizePath(x.FileName), fileName, StringComparison.OrdinalIgnoreCase));
        }

        private static (string Status, string Message) ValidateBlockAgainstContent(
            string diffBlock,
            string content)
        {
            var sourceLines = SplitLines(content).ToList();
            int cursor = 0;
            int checkedLines = 0;
            int fuzzyMatches = 0;

            foreach (string raw in SplitLines(diffBlock))
            {
                if (string.IsNullOrWhiteSpace(raw) ||
                    raw.StartsWith("diff --git ", StringComparison.Ordinal) ||
                    raw.StartsWith("index ", StringComparison.Ordinal) ||
                    raw.StartsWith("@@", StringComparison.Ordinal) ||
                    raw.StartsWith("---", StringComparison.Ordinal) ||
                    raw.StartsWith("+++", StringComparison.Ordinal))
                {
                    continue;
                }

                if (raw.StartsWith("+", StringComparison.Ordinal))
                    continue;

                if (!raw.StartsWith("-", StringComparison.Ordinal) &&
                    !raw.StartsWith(" ", StringComparison.Ordinal))
                {
                    continue;
                }

                string expected = raw.Substring(1);
                var found = FindLine(sourceLines, expected, cursor);
                if (found.Index < 0)
                {
                    return ("invalid", $"Could not find expected source line near diff context: {Trim(expected, 120)}");
                }

                cursor = found.Index + 1;
                checkedLines++;

                if (found.Fuzzy)
                    fuzzyMatches++;
            }

            if (checkedLines == 0)
                return ("invalid", "Diff block has no removable/context source lines to validate.");

            if (fuzzyMatches > 0)
            {
                return (
                    "warning",
                    $"Validated {checkedLines} source line(s), but {fuzzyMatches} matched only after whitespace normalization.");
            }

            return ("valid", $"Validated {checkedLines} source line(s) against attachment snapshot.");
        }

        private static (int Index, bool Fuzzy) FindLine(IReadOnlyList<string> lines, string expected, int start)
        {
            for (int i = Math.Max(0, start); i < lines.Count; i++)
            {
                if (string.Equals(lines[i], expected, StringComparison.Ordinal))
                    return (i, false);
            }

            string normalizedExpected = NormalizeCodeLine(expected);
            if (string.IsNullOrWhiteSpace(normalizedExpected))
                return (-1, false);

            for (int i = Math.Max(0, start); i < lines.Count; i++)
            {
                if (string.Equals(
                    NormalizeCodeLine(lines[i]),
                    normalizedExpected,
                    StringComparison.Ordinal))
                {
                    return (i, true);
                }
            }

            return (-1, false);
        }

        private static string NormalizeCodeLine(string? line)
            => Regex.Replace((line ?? "").Trim(), @"\s+", " ");

        private static (int Added, int Removed) CountChangedLines(string diffBlock)
        {
            int added = 0;
            int removed = 0;

            foreach (string line in SplitLines(diffBlock))
            {
                if (line.StartsWith("+++", StringComparison.Ordinal) ||
                    line.StartsWith("---", StringComparison.Ordinal))
                    continue;

                if (line.StartsWith("+", StringComparison.Ordinal))
                    added++;
                else if (line.StartsWith("-", StringComparison.Ordinal))
                    removed++;
            }

            return (added, removed);
        }

        private static IEnumerable<string> SplitLines(string? text)
            => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');

        private static string NormalizePath(string? path)
            => (path ?? "").Trim().Replace('\\', '/').TrimStart('/');

        private static string Trim(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            string trimmed = text.Trim();
            return trimmed.Length <= max ? trimmed : trimmed.Substring(0, max) + "...";
        }
    }
}
