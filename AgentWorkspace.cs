using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public sealed class AgentWorkspace
    {
        private readonly object _sync = new();
        private readonly List<AgentWorkspaceItem> _items = new();

        public string RunId { get; } = Guid.NewGuid().ToString("N");

        public void Add(AgentWorkspaceItem item)
        {
            if (item == null)
                return;

            lock (_sync)
            {
                _items.Add(item);
            }
        }

        public IReadOnlyList<AgentWorkspaceItem> GetAll()
        {
            lock (_sync)
            {
                return _items.ToList();
            }
        }

        public IReadOnlyList<AgentWorkspaceItem> GetByType(string itemType)
        {
            if (string.IsNullOrWhiteSpace(itemType))
                return Array.Empty<AgentWorkspaceItem>();

            lock (_sync)
            {
                return _items
                    .Where(x => string.Equals(x.ItemType, itemType, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }
        }

        public string BuildPromptBlock()
        {
            var items = GetAll();
            if (items.Count == 0)
                return "";

            var lines = new List<string>
            {
                "【Agent Workspace】",
                "以下是本次 agent run 中已產生的共享工作區資料。可用來整合回答，但不可直接輸出內部欄位名稱。"
            };

            foreach (var item in items)
            {
                lines.Add($"- Type: {item.ItemType}");
                lines.Add($"  Source Agent: {item.SourceAgentId}");
                lines.Add($"  Title: {item.Title}");

                if (!string.IsNullOrWhiteSpace(item.TextSummary))
                    lines.Add($"  Summary: {item.TextSummary}");
            }

            return string.Join(Environment.NewLine, lines);
        }
        public AgentWorkspaceSummary BuildSummary()
        {
            var items = GetAll();

            if (items.Count == 0)
            {
                return new AgentWorkspaceSummary
                {
                    RunId = RunId,
                    SummaryText = "本次 agent run 沒有產生 workspace item。"
                };
            }

            var itemTypes = items
                .Select(x => x.ItemType)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var sourceAgents = items
                .Select(x => x.SourceAgentId)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var delegateModels = items
    .Where(x => string.Equals(x.ItemType, "delegate_output", StringComparison.OrdinalIgnoreCase))
    .Select(x => x.Payload)
    .OfType<DelegateOutputPayload>()
    .Where(x => !string.IsNullOrWhiteSpace(x.ActualModelId))
    .Select(x => $"{x.ToAgentId}={x.ActualModelId}")
    .Distinct(StringComparer.OrdinalIgnoreCase)
    .ToList();

            var lines = new List<string>
{
    $"本次 agent run 共產生 {items.Count} 個 workspace item。",
    $"Item Types: {string.Join(", ", itemTypes)}",
    $"Source Agents: {string.Join(", ", sourceAgents)}"
}; 
            if (delegateModels.Count > 0)
                lines.Add($"Delegate Models: {string.Join(", ", delegateModels)}");

            return new AgentWorkspaceSummary
            {
                RunId = RunId,
                ItemTypes = itemTypes,
                SourceAgents = sourceAgents,
                SummaryText = string.Join(Environment.NewLine, lines)
            };
        }

        private static string Trim(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            return text.Length <= max
                ? text
                : text.Substring(0, max) + "…";
        }

    }
}