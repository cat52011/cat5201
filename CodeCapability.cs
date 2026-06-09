using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class CodeCapability : IAgentCapability
    {
        private const int MaxDiffContextCharsPerFile = 12000;

        public string Id => "code-capability";

        public AgentCapability RequiredAgentCapability => AgentCapability.CodeTool;

        public bool CanHandle(AgentExecutionContext context)
        {
            if (context == null)
                return false;

            if (context.TaskMode == NodeTaskMode.Code)
                return true;

            string text = context.TopText ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(text,
                "程式", "程式碼", "code", "bug", "debug", "錯誤", "修正",
                "class", "method", "function", "exception",
                "c#", "xaml", ".net", "wpf", "python", "c++",
                "compile", "build", "namespace", "null");
        }

        public Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct)
        {
            string text = context.TopText ?? "";
            string requestType = ResolveRequestType(text);

            var payload = new CodeAnalysisPayload
            {
                RequestType = requestType,
                Language = ResolveLanguage(text),
                UserGoal = Trim(text, 500),
                DetectedSignals = DetectSignals(text),
                RequiredActions = BuildRequiredActions(text),
                Guidance = BuildGuidance(text)
            };

            var data = new Dictionary<string, object>
            {
                ["code_analysis"] = payload
            };

            var diffDraft = TryBuildDiffDraft(context, requestType);
            if (diffDraft != null)
                data["code_diff_draft"] = diffDraft;

            return Task.FromResult(
                new AgentCapabilityResult
                {
                    Handled = true,
                    Data = data
                });
        }

        private static string ResolveRequestType(string text)
        {
            if (ContainsAny(text, "修正", "fix", "bug", "debug", "錯誤", "exception", "null"))
                return "debug_or_fix";

            if (ContainsAny(text, "解釋", "說明", "explain", "why"))
                return "explain";

            if (ContainsAny(text, "重構", "refactor", "架構", "architecture"))
                return "refactor_or_architecture";

            if (ContainsAny(text, "修改", "改成", "加入", "新增", "modify", "update"))
                return "modify";

            if (ContainsAny(text, "完整程式", "完整程式碼", "可直接貼上", "貼上即用"))
                return "full_code";

            return "code_generation";
        }

        private static string ResolveLanguage(string text)
        {
            if (ContainsAny(text, "xaml"))
                return "XAML";

            if (ContainsAny(text, "c#", ".net", "wpf", "namespace"))
                return "C#";

            if (ContainsAny(text, "python"))
                return "Python";

            if (ContainsAny(text, "c++", "cpp"))
                return "C++";

            if (ContainsAny(text, "javascript", "typescript", "js", "ts"))
                return "JavaScript/TypeScript";

            return "unknown";
        }

        private static IReadOnlyList<string> DetectSignals(string text)
        {
            var result = new List<string>();

            AddIf(result, text, "WPF", "wpf");
            AddIf(result, text, ".NET", ".net");
            AddIf(result, text, "XAML", "xaml");
            AddIf(result, text, "Null risk", "null");
            AddIf(result, text, "Exception", "exception", "錯誤");
            AddIf(result, text, "Direct paste required", "完整程式", "完整程式碼", "可直接貼上", "貼上即用");
            AddIf(result, text, "Debug request", "debug", "bug", "修正");
            AddIf(result, text, "Refactor request", "重構", "refactor");

            return result;
        }

        private static IReadOnlyList<string> BuildRequiredActions(string text)
        {
            var actions = new List<string>();

            if (ContainsAny(text, "完整程式", "完整程式碼", "可直接貼上", "貼上即用"))
                actions.Add("Provide complete paste-ready code.");

            if (ContainsAny(text, "修正", "debug", "bug", "錯誤", "exception", "null"))
                actions.Add("Identify likely cause and provide corrected code.");

            if (ContainsAny(text, "解釋", "說明", "explain"))
                actions.Add("Explain the code behavior clearly.");

            if (ContainsAny(text, "修改", "新增", "加入", "改成"))
                actions.Add("Modify existing structure without breaking current architecture.");

            if (actions.Count == 0)
                actions.Add("Generate code that satisfies the user request.");

            return actions;
        }

        private static CodeDiffArtifactPayload? TryBuildDiffDraft(
            AgentExecutionContext context,
            string requestType)
        {
            if (context == null || context.Attachments == null || context.Attachments.Count == 0)
                return null;

            if (!ShouldCreateDiffDraft(context.TopText ?? "", requestType))
                return null;

            var files = new List<CodeDiffFileChange>();
            var diff = new List<string>();

            foreach (var attachment in context.Attachments.Take(8))
            {
                if (attachment == null)
                    continue;

                string fileName = attachment.FileName ?? "";
                string relativePath = attachment.RelativePath ?? "";

                if (!IsTextCodeFile(fileName, attachment.MimeType ?? ""))
                    continue;

                string? content = TryReadAttachmentText(
                    context.AttachmentsRootDir,
                    relativePath);

                if (string.IsNullOrWhiteSpace(content))
                    continue;

                int lineCount = CountLines(content);
                string path = string.IsNullOrWhiteSpace(relativePath) ? fileName : relativePath;

                files.Add(new CodeDiffFileChange
                {
                    Path = path,
                    ChangeType = "modify",
                    AddedLines = 0,
                    RemovedLines = 0,
                    Summary = "Draft target: model should propose a focused unified diff for this file based on the user request."
                });

                diff.Add($"diff --git a/{path} b/{path}");
                diff.Add($"--- a/{path}");
                diff.Add($"+++ b/{path}");
                diff.Add("@@ draft @@");
                diff.Add($"# {lineCount} source line(s) loaded from attachment snapshot.");
                diff.Add("# Actual additions/removals must be produced by the model response or later sandbox step.");
                diff.Add("");
            }

            if (files.Count == 0)
                return null;

            return new CodeDiffArtifactPayload
            {
                Title = $"Code Diff Draft - {ResolveShortGoal(context.TopText ?? "")}",
                Status = "draft",
                BaseLabel = "attached snapshot",
                TargetLabel = "requested change",
                Files = files,
                UnifiedDiff = string.Join(Environment.NewLine, diff).Trim(),
                Notes = new[]
                {
                    "This artifact is a non-applied diff draft. It records candidate files and patch intent only.",
                    "Do not treat the draft hunk as an applied patch.",
                    "A later sandbox/apply step should validate and materialize exact edits."
                }
            };
        }

        private static bool ShouldCreateDiffDraft(string text, string requestType)
        {
            if (ContainsAny(text, "不要改", "只解釋", "只說明", "explain only"))
                return false;

            return string.Equals(requestType, "debug_or_fix", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(requestType, "modify", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(requestType, "refactor_or_architecture", StringComparison.OrdinalIgnoreCase) ||
                   ContainsAny(text, "patch", "diff", "修改", "修正", "新增", "加入", "改成", "重構");
        }

        private static bool IsTextCodeFile(string fileName, string mimeType)
        {
            string ext = Path.GetExtension(fileName ?? "") ?? "";

            return string.Equals(ext, ".java", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".cs", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".xaml", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".cpp", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".py", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".js", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".ts", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".json", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".md", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(ext, ".txt", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mimeType, "text/plain", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(mimeType, "application/json", StringComparison.OrdinalIgnoreCase);
        }

        private static string? TryReadAttachmentText(
            string attachmentsRootDir,
            string relativePath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(attachmentsRootDir) ||
                    string.IsNullOrWhiteSpace(relativePath))
                {
                    return null;
                }

                string root = Path.GetFullPath(attachmentsRootDir);
                string fullPath = Path.GetFullPath(Path.Combine(root, relativePath));

                if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase) ||
                    !File.Exists(fullPath))
                {
                    return null;
                }

                string content = File.ReadAllText(fullPath);
                return content.Length <= MaxDiffContextCharsPerFile
                    ? content
                    : content.Substring(0, MaxDiffContextCharsPerFile);
            }
            catch
            {
                return null;
            }
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

        private static string ResolveShortGoal(string text)
        {
            string trimmed = Trim(text, 64);
            return string.IsNullOrWhiteSpace(trimmed) ? "requested code change" : trimmed;
        }

        private static string BuildGuidance(string text)
        {
            return
                "回答時請優先維持使用者現有架構。若提供程式碼，需可直接貼上使用。" +
                "若資訊不足，請明確指出需要哪個檔案或方法，不要捏造不存在的類別。" +
                "若是 WPF/.NET 8/C#/XAML 任務，請符合該專案環境。";
        }

        private static void AddIf(List<string> result, string text, string label, params string[] keywords)
        {
            if (ContainsAny(text, keywords))
                result.Add(label);
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword) &&
                    text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            return text.Length <= max ? text : text.Substring(0, max) + "...";
        }
    }
}
