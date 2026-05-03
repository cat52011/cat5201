using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class SearchCapability : IAgentCapability
    {
        private readonly PerplexityService _service;

        public SearchCapability(PerplexityService service)
        {
            _service = service;
        }

        public string Id => "search-capability";

        public AgentCapability RequiredAgentCapability => AgentCapability.Search;

        public bool CanHandle(AgentExecutionContext context)
        {
            if (context == null)
                return false;

            if (context.TaskMode == NodeTaskMode.Research)
                return true;

            string text = context.TopText ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("查詢") ||
                   text.Contains("搜尋") ||
                   text.Contains("查證") ||
                   text.Contains("最新") ||
                   text.Contains("比較") ||
                   text.Contains("research", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("latest", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct)
        {
            var results = await _service.SearchAsync(
                context.TopText,
                maxResults: 5,
                ct: ct);

            if (results == null || results.Count == 0)
                return AgentCapabilityResult.NotHandled();

            var cleaned = results
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Title))
                .GroupBy(x => x.Title.Trim(), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (cleaned.Count == 0)
                return AgentCapabilityResult.NotHandled();

            var items = cleaned
                .Select(x => new SearchSummaryItem
                {
                    Title = x.Title ?? "",
                    KeyPoint = ExtractKeyPoint(x.Snippet),
                    Source = x.Url ?? "",
                    Date = x.Date ?? ""
                })
                .ToList();

            string summary = BuildSummary(context.TopText, items);

            var payload = new SearchSummaryPayload
            {
                Query = context.TopText ?? "",
                Summary = summary,
                Items = items
            };

            return AgentCapabilityResult.WithData("search_summary", payload);
        }

        private static string ExtractKeyPoint(string snippet)
        {
            if (string.IsNullOrWhiteSpace(snippet))
                return "";

            snippet = snippet.Trim();

            return snippet.Length > 100
                ? snippet.Substring(0, 100)
                : snippet;
        }

        private static string BuildSummary(string query, IEnumerable<SearchSummaryItem> items)
        {
            var sb = new StringBuilder();
            sb.AppendLine("整理重點如下：");

            int index = 1;
            foreach (var item in items)
            {
                if (item == null || string.IsNullOrWhiteSpace(item.KeyPoint))
                    continue;

                sb.AppendLine($"{index}. {item.KeyPoint}");
                index++;
            }

            if (index == 1)
                return "無明確重點資訊";

            return sb.ToString().Trim();
        }
    }
}