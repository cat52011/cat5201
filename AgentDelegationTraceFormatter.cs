using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class AgentDelegationTraceFormatter
    {
        public static string BuildSummary(IReadOnlyList<AgentDelegationTraceItem> items)
        {
            if (items == null || items.Count == 0)
                return "";

            var parts = new List<string>();

            foreach (var item in items)
            {
                string from = string.IsNullOrWhiteSpace(item.FromAgentId) ? "-" : item.FromAgentId;
                string to = string.IsNullOrWhiteSpace(item.ToAgentId) ? "-" : item.ToAgentId;
                string state = item.Success ? "✅" : "❌";

                parts.Add($"{from} → {to} {state}");
            }

            return string.Join(" | ", parts);
        }

        public static IReadOnlyList<string> BuildDetailLines(IReadOnlyList<AgentDelegationTraceItem> items)
        {
            if (items == null || items.Count == 0)
                return new List<string>();

            return items.Select(item =>
            {
                string indent = new string(' ', item.Depth * 2);
                string route = $"{indent}{item.FromAgentId} -> {item.ToAgentId}";
                string instruction = string.IsNullOrWhiteSpace(item.Instruction) ? "-" : Trim(item.Instruction, 80);
                string output = string.IsNullOrWhiteSpace(item.OutputSummary) ? "-" : Trim(item.OutputSummary, 100);

                if (item.Success)
                    return $"{route} / instruction: {instruction} / output: {output}";

                string error = string.IsNullOrWhiteSpace(item.ErrorMessage) ? "-" : Trim(item.ErrorMessage, 80);
                return $"{route} / instruction: {instruction} / error: {error}";
            }).ToList();
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