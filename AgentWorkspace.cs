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

            var verifiedFacts = items
    .Where(x => string.Equals(x.ItemType, "verified_facts", StringComparison.OrdinalIgnoreCase))
    .ToList();

            var searchSummaries = items
                .Where(x => string.Equals(x.ItemType, "search_summary", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var researchSearchSummaries = searchSummaries
                .Where(x => string.Equals(x.SourceAgentId, "research-agent", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var analysis = items
                .Where(x =>
                    !string.Equals(x.ItemType, "verified_facts", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(x.ItemType, "search_summary", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var lines = new List<string>();

            if (verifiedFacts.Count > 0)
            {
                lines.Add("【Verified Facts】");
                lines.Add("以下資料是唯一可用來回答數字、價格、日期、財報、即時資訊的事實來源。若其他區塊與此區衝突，必須以此區為準。");

                foreach (var item in verifiedFacts)
                {
                    if (item.Payload is VerifiedFactPayload payload)
                    {
                        if (!string.IsNullOrWhiteSpace(payload.Summary))
                            lines.Add(payload.Summary);

                        foreach (var fact in payload.Facts)
                        {
                            lines.Add($"- Subject: {fact.Subject}");
                            lines.Add($"  Type: {fact.FactType}");
                            lines.Add($"  Value: {fact.Value} {fact.Unit}".Trim());
                            if (!string.IsNullOrWhiteSpace(fact.AsOf))
                                lines.Add($"  AsOf: {fact.AsOf}");
                            if (!string.IsNullOrWhiteSpace(fact.SourceTitle))
                                lines.Add($"  Source: {fact.SourceTitle}");
                            if (!string.IsNullOrWhiteSpace(fact.SourceUrl))
                                lines.Add($"  Url: {fact.SourceUrl}");
                            lines.Add($"  Confidence: {fact.Confidence}");
                        }
                    }
                    else if (!string.IsNullOrWhiteSpace(item.TextSummary))
                    {
                        lines.Add(item.TextSummary);
                    }
                }
            }
            else if (researchSearchSummaries.Count > 0)
            {
                lines.Add("【Verified Facts】");
                lines.Add("目前沒有獨立 verified_facts payload；以下 research-agent 的 search_summary 暫時作為唯一事實來源。其他 agent 的輸出不可覆蓋此區。");

                foreach (var item in researchSearchSummaries)
                {
                    if (!string.IsNullOrWhiteSpace(item.TextSummary))
                        lines.Add(item.TextSummary);
                }
            }

            if (searchSummaries.Count > 0)
            {
                lines.Add("");
                lines.Add("【Search Context】");
                lines.Add("以下搜尋摘要僅可作為背景脈絡。若要使用數字、價格、日期或財報數據，必須以 Verified Facts 為準。");

                foreach (var item in searchSummaries)
                {
                    if (!string.IsNullOrWhiteSpace(item.TextSummary))
                        lines.Add(item.TextSummary);
                }
            }

            if (analysis.Count > 0)
            {
                lines.Add("");
                lines.Add("【Analysis Context】");
                lines.Add("以下內容只能用於推論、比較、整理與風險分析，不可新增或覆蓋任何事實數字。");

                foreach (var item in analysis)
                {
                    lines.Add($"- Type: {item.ItemType}");
                    lines.Add($"  Source Agent: {item.SourceAgentId}");

                    if (!string.IsNullOrWhiteSpace(item.TextSummary))
                        lines.Add($"  Summary: {item.TextSummary}");
                }
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
                .Where(x =>
                    string.Equals(x.ItemType, "delegate_output", StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(x.ItemType, "parallel_agent_output", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Payload)
                .OfType<DelegateOutputPayload>()
                .Where(x => !string.IsNullOrWhiteSpace(x.ActualModelId))
                .Select(x => $"{x.ToAgentId}={x.ActualModelId}")
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var finalSynthesis = items
                .Select(x => x.Payload)
                .OfType<FinalSynthesisPayload>()
                .FirstOrDefault(x => x.Success);

            var lines = new List<string>
    {
        $"多代理協作：{sourceAgents.Count} 個 agent 參與",
        $"共享成果：{items.Count} 項",
        $"資料類型：{string.Join(", ", itemTypes)}",
        $"參與代理：{string.Join(", ", sourceAgents)}"
    };

            if (delegateModels.Count > 0)
                lines.Add($"代理模型：{string.Join(", ", delegateModels)}");

            if (finalSynthesis != null)
                lines.Add($"最終整合：{finalSynthesis.SynthesizerAgentId} / {finalSynthesis.ModelId}");

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