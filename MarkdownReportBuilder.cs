using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace test
{
    /// <summary>
    /// File Generation v1：把 final answer + shared workspace 組成一份人類可讀的 Markdown 報告。
    /// 純字串組裝，無外部依賴。報告結構：標題 → 中繼資料 → 正文（final answer）→ 來源（verified_facts）。
    /// </summary>
    public static class MarkdownReportBuilder
    {
        public sealed class Request
        {
            public string UserInput { get; init; } = "";
            public string FinalAnswer { get; init; } = "";
            public AgentWorkspace? Workspace { get; init; }
            public OrchestrationTaskType TaskType { get; init; }
            public string PipelineId { get; init; } = "";
            public string ModelId { get; init; } = "";
            public string AgentId { get; init; } = "";
        }

        public static string Build(Request request)
        {
            request ??= new Request();

            var sb = new StringBuilder();

            string title = BuildTitle(request.UserInput);
            sb.AppendLine($"# {title}");
            sb.AppendLine();

            // 中繼資料區（產生時間、模型、管線），讓報告本身可追溯。
            sb.AppendLine("> " + string.Join(" ｜ ", BuildMetadataParts(request)));
            sb.AppendLine();

            sb.AppendLine("## 內容");
            sb.AppendLine();
            string body = (request.FinalAnswer ?? "").Trim();
            sb.AppendLine(string.IsNullOrWhiteSpace(body) ? "（本次未產生內容。）" : body);
            sb.AppendLine();

            AppendSources(sb, request.Workspace);

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        private static IReadOnlyList<string> BuildMetadataParts(Request request)
        {
            var parts = new List<string>
            {
                $"產生時間：{DateTime.Now:yyyy-MM-dd HH:mm}"
            };

            if (!string.IsNullOrWhiteSpace(request.AgentId))
                parts.Add($"代理：{request.AgentId}");

            if (!string.IsNullOrWhiteSpace(request.ModelId))
                parts.Add($"模型：{request.ModelId}");

            if (!string.IsNullOrWhiteSpace(request.PipelineId))
                parts.Add($"管線：{request.PipelineId}");

            return parts;
        }

        private static void AppendSources(StringBuilder sb, AgentWorkspace? workspace)
        {
            if (workspace == null)
                return;

            var facts = workspace.GetByType("verified_facts")
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .SelectMany(p => p.Facts ?? Array.Empty<VerifiedFactItem>())
                .Where(f => f != null)
                .ToList();

            if (facts.Count == 0)
                return;

            // 只列出有來源標題或網址、且為數字事實來源的條目，避免報告尾巴塞滿背景雜訊。
            var sourced = facts
                .Where(f =>
                    !string.IsNullOrWhiteSpace(f.SourceTitle) ||
                    !string.IsNullOrWhiteSpace(f.SourceUrl))
                .GroupBy(f => (f.SourceTitle ?? "").Trim() + "|" + (f.SourceUrl ?? "").Trim())
                .Select(g => g.First())
                .Take(20)
                .ToList();

            if (sourced.Count == 0)
                return;

            sb.AppendLine("## 資料來源");
            sb.AppendLine();

            int i = 1;
            foreach (var f in sourced)
            {
                string label = string.IsNullOrWhiteSpace(f.SourceTitle)
                    ? f.SourceUrl
                    : f.SourceTitle.Trim();

                string line = string.IsNullOrWhiteSpace(f.SourceUrl)
                    ? $"{i}. {label}"
                    : $"{i}. [{label}]({f.SourceUrl.Trim()})";

                if (!string.IsNullOrWhiteSpace(f.AsOf))
                    line += $"（{f.AsOf.Trim()}）";

                sb.AppendLine(line);
                i++;
            }

            sb.AppendLine();
        }

        private static string BuildTitle(string userInput)
        {
            string text = (userInput ?? "").Replace("\r", " ").Replace("\n", " ").Trim();

            if (string.IsNullOrWhiteSpace(text))
                return "報告";

            // 取第一行 / 前 40 字作為標題，避免整段提示變成超長標題。
            if (text.Length > 40)
                text = text.Substring(0, 40).TrimEnd() + "…";

            return text;
        }
    }
}
