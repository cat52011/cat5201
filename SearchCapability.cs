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
                   text.Contains("research", System.StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("search", System.StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("latest", System.StringComparison.OrdinalIgnoreCase);
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

            var sb = new StringBuilder();
            sb.AppendLine("【Capability Search Results】");

            int index = 1;
            foreach (var item in results)
            {
                sb.AppendLine($"{index}. {item.Title}");
                if (!string.IsNullOrWhiteSpace(item.Snippet))
                    sb.AppendLine($"   Snippet: {item.Snippet}");
                if (!string.IsNullOrWhiteSpace(item.Url))
                    sb.AppendLine($"   Url: {item.Url}");
                if (!string.IsNullOrWhiteSpace(item.Date))
                    sb.AppendLine($"   Date: {item.Date}");

                index++;
            }

            string augmented =
                context.TopText +
                "\n\n" +
                sb.ToString() +
                "\n請基於以上搜尋結果完成回答。";

            return AgentCapabilityResult.WithAugmentedPrompt(augmented);
        }
    }
}