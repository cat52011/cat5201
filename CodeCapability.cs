using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class CodeCapability : IAgentCapability
    {
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

            var payload = new CodeAnalysisPayload
            {
                RequestType = ResolveRequestType(text),
                Language = ResolveLanguage(text),
                UserGoal = Trim(text, 500),
                DetectedSignals = DetectSignals(text),
                RequiredActions = BuildRequiredActions(text),
                Guidance = BuildGuidance(text)
            };

            return Task.FromResult(
                AgentCapabilityResult.WithData("code_analysis", payload));
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
            return text.Length <= max ? text : text.Substring(0, max) + "…";
        }
    }
}