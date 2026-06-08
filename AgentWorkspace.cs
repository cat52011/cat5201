using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

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

            var verifiedFactPayloads = items
                .Where(x => string.Equals(x.ItemType, "verified_facts", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .ToList();

            var searchSummaryPayloads = items
                .Where(x => string.Equals(x.ItemType, "search_summary", StringComparison.OrdinalIgnoreCase))
                .Select(x => x.Payload)
                .OfType<SearchSummaryPayload>()
                .ToList();

            var searchSummaryItems = items
                .Where(x => string.Equals(x.ItemType, "search_summary", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var analysisItems = items
                .Where(x =>
                    !string.Equals(x.ItemType, "verified_facts", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(x.ItemType, "search_summary", StringComparison.OrdinalIgnoreCase) &&
                    !string.Equals(x.ItemType, "final_synthesis", StringComparison.OrdinalIgnoreCase))
                .ToList();

            var lines = new List<string>();

            if (verifiedFactPayloads.Count > 0)
            {
                lines.Add("【Verified Facts】");
                lines.Add("以下是唯一可用於數字、價格、日期、財報、即時資訊的事實來源。Final answer 不可自行發明或改寫不存在的數字。");
                lines.Add("請優先使用 High / Medium confidence facts。若同一 subject + fact type 有多個不同 value，代表來源衝突， final answer 必須簡短合併說明，不要逐條展開成冗長清單。");
                lines.Add("");

                foreach (var payload in verifiedFactPayloads)
                {
                    if (!string.IsNullOrWhiteSpace(payload.Query))
                        lines.Add($"Query: {payload.Query}");

                    if (!string.IsNullOrWhiteSpace(payload.Summary))
                    {
                        lines.Add("Research Summary:");
                        lines.Add(Trim(payload.Summary, 1200));
                        lines.Add("");
                    }

                    var facts = payload.Facts?
                        .Where(x => x != null)
                        .ToList() ?? new List<VerifiedFactItem>();

                    AppendGroupedVerifiedFacts(lines, facts);
                }
            }
            else if (searchSummaryPayloads.Count > 0 || searchSummaryItems.Count > 0)
            {
                lines.Add("【Verified Facts】");
                lines.Add("目前沒有獨立 verified_facts payload；以下 research-agent / search_summary 暫時作為唯一事實來源。其他 agent 的輸出不可覆蓋此區。");
                lines.Add("");

                foreach (var payload in searchSummaryPayloads)
                {
                    if (!string.IsNullOrWhiteSpace(payload.Query))
                        lines.Add($"Query: {payload.Query}");

                    if (!string.IsNullOrWhiteSpace(payload.Summary))
                        lines.Add(Trim(payload.Summary, 1500));

                    if (payload.Items != null && payload.Items.Count > 0)
                    {
                        foreach (var item in payload.Items.Take(12))
                        {
                            lines.Add($"- {Safe(item.Title)}");
                            if (!string.IsNullOrWhiteSpace(item.KeyPoint))
                                lines.Add($"  KeyPoint: {Trim(item.KeyPoint, 300)}");
                            if (!string.IsNullOrWhiteSpace(item.Date))
                                lines.Add($"  Date: {item.Date}");
                            if (!string.IsNullOrWhiteSpace(item.Source))
                                lines.Add($"  Source: {item.Source}");
                        }
                    }

                    lines.Add("");
                }

                foreach (var item in searchSummaryItems)
                {
                    if (!string.IsNullOrWhiteSpace(item.TextSummary))
                        lines.Add(Trim(item.TextSummary, 1500));
                }
            }

            if (searchSummaryPayloads.Count > 0)
            {
                lines.Add("");
                lines.Add("【Search Context】");
                lines.Add("以下只作為背景脈絡。若與 Verified Facts 衝突，以 Verified Facts 為準。Final answer 不要原封不動列出所有搜尋摘要。");

                foreach (var payload in searchSummaryPayloads)
                {
                    if (!string.IsNullOrWhiteSpace(payload.Summary))
                    {
                        lines.Add($"- {Trim(payload.Summary, 800)}");
                    }

                    if (payload.Items != null)
                    {
                        foreach (var item in payload.Items.Take(8))
                        {
                            var title = Safe(item.Title);
                            var point = Trim(item.KeyPoint, 220);
                            var date = Safe(item.Date);

                            if (!string.IsNullOrWhiteSpace(title) || !string.IsNullOrWhiteSpace(point))
                                lines.Add($"- {title}" + (string.IsNullOrWhiteSpace(point) ? "" : $"：{point}") + (string.IsNullOrWhiteSpace(date) ? "" : $" ({date})"));
                        }
                    }
                }
            }

            if (analysisItems.Count > 0)
            {
                lines.Add("");
                lines.Add("【Analysis Context】");
                lines.Add("以下內容只能用於推論、比較、整理與風險分析，不可新增或覆蓋任何價格、日期、EPS、營收、毛利率、指引等事實數字。Final answer 應吸收其重點，不要逐條列出內部 artifact。");

                foreach (var item in analysisItems.Take(10))
                {
                    lines.Add($"- Type: {item.ItemType}");
                    lines.Add($"  Source Agent: {item.SourceAgentId}");

                    if (!string.IsNullOrWhiteSpace(item.TextSummary))
                        lines.Add($"  Summary: {Trim(item.TextSummary, 700)}");
                }
            }

            return string.Join(Environment.NewLine, lines).Trim();
        }

        private static void AppendGroupedVerifiedFacts(List<string> lines, IReadOnlyList<VerifiedFactItem> facts)
        {
            if (facts == null || facts.Count == 0)
            {
                lines.Add("No structured fact items.");
                return;
            }

            var grouped = facts
                .GroupBy(x => new
                {
                    Subject = NormalizeKey(x.Subject),
                    FactType = NormalizeKey(x.FactType)
                })
                .OrderBy(g => g.Key.Subject)
                .ThenBy(g => g.Key.FactType)
                .ToList();

            foreach (var group in grouped)
            {
                var subject = FirstNonEmpty(group.Select(x => x.Subject));
                var factType = FirstNonEmpty(group.Select(x => x.FactType));

                if (string.IsNullOrWhiteSpace(subject))
                    subject = "Unknown Subject";

                if (string.IsNullOrWhiteSpace(factType))
                    factType = "general";

                lines.Add($"Subject: {subject}");
                lines.Add($"FactType: {factType}");

                var valueGroups = group
                    .GroupBy(x => NormalizeValue(x.Value, x.Unit))
                    .OrderByDescending(g => ConfidenceScore(g.Select(x => x.Confidence)))
                    .ThenByDescending(g => g.Count())
                    .ToList();

                if (valueGroups.Count == 1)
                {
                    var valueGroup = valueGroups[0];
                    var first = valueGroup.First();

                    lines.Add($"Value: {FormatValue(first.Value, first.Unit)}");

                    var asOf = FirstNonEmpty(valueGroup.Select(x => x.AsOf));
                    if (!string.IsNullOrWhiteSpace(asOf))
                        lines.Add($"AsOf: {asOf}");

                    var confidence = BestConfidence(valueGroup.Select(x => x.Confidence));
                    lines.Add($"Confidence: {confidence}");

                    var sources = valueGroup
                        .Select(FormatSource)
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .Take(3)
                        .ToList();

                    if (sources.Count > 0)
                        lines.Add($"Sources: {string.Join(" | ", sources)}");
                }
                else
                {
                    lines.Add("Conflict: true");
                    lines.Add("Values:");

                    foreach (var valueGroup in valueGroups.Take(6))
                    {
                        var first = valueGroup.First();
                        var asOf = FirstNonEmpty(valueGroup.Select(x => x.AsOf));
                        var confidence = BestConfidence(valueGroup.Select(x => x.Confidence));

                        var source = valueGroup
                            .Select(FormatSource)
                            .Where(x => !string.IsNullOrWhiteSpace(x))
                            .Distinct(StringComparer.OrdinalIgnoreCase)
                            .FirstOrDefault() ?? "";

                        var line = $"  - {FormatValue(first.Value, first.Unit)}";

                        if (!string.IsNullOrWhiteSpace(asOf))
                            line += $" | AsOf: {asOf}";

                        line += $" | Confidence: {confidence}";

                        if (!string.IsNullOrWhiteSpace(source))
                            line += $" | Source: {source}";

                        lines.Add(line);
                    }

                    lines.Add("Instruction: final answer must summarize this conflict briefly and must not merge these values into one number.");
                }

                lines.Add("");
            }
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

        private static string NormalizeKey(string? text)
        {
            return (text ?? "").Trim().ToLowerInvariant();
        }

        private static string NormalizeValue(string? value, string? unit)
        {
            return $"{(value ?? "").Trim()} {(unit ?? "").Trim()}".Trim().ToLowerInvariant();
        }

        private static string FormatValue(string? value, string? unit)
        {
            var v = (value ?? "").Trim();
            var u = (unit ?? "").Trim();

            if (string.IsNullOrWhiteSpace(v))
                return "";

            return string.IsNullOrWhiteSpace(u)
                ? v
                : $"{v} {u}";
        }

        private static string FormatSource(VerifiedFactItem fact)
        {
            if (fact == null)
                return "";

            var title = Safe(fact.SourceTitle);
            var url = Safe(fact.SourceUrl);

            if (!string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(url))
                return $"{title} ({url})";

            if (!string.IsNullOrWhiteSpace(title))
                return title;

            return url;
        }

        private static string FirstNonEmpty(IEnumerable<string?> values)
        {
            return values?
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x))
                ?.Trim() ?? "";
        }

        private static string BestConfidence(IEnumerable<string?> values)
        {
            var list = values?
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Select(x => x!.Trim().ToLowerInvariant())
                .ToList() ?? new List<string>();

            if (list.Contains("high"))
                return "high";

            if (list.Contains("medium"))
                return "medium";

            if (list.Contains("low"))
                return "low";

            return list.FirstOrDefault() ?? "medium";
        }

        private static int ConfidenceScore(IEnumerable<string?> values)
        {
            var best = BestConfidence(values);

            return best switch
            {
                "high" => 3,
                "medium" => 2,
                "low" => 1,
                _ => 0
            };
        }

        private static string Safe(string? text)
        {
            return (text ?? "").Trim();
        }

        private static string Trim(string? text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();

            if (max <= 0 || text.Length <= max)
                return text;

            return text.Substring(0, max) + "…";
        }
    }
}