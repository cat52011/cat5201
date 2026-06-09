using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class AgentCapabilityPlanner
    {
        public static AgentCapabilityExecutionPlan Build(
            AgentDefinition agent,
            string topText,
            NodeTaskMode taskMode,
            bool hasAttachments)
        {
            topText ??= "";

            bool needsFreshFacts =
                FinanceTaskDetector.RequiresFreshFacts(topText, taskMode) ||
                ContainsAny(topText,
                    "最新", "即時", "今天", "現在", "目前",
                    "股價", "財報", "新聞", "市場", "匯率", "天氣",
                    "查詢", "搜尋", "查證",
                    "latest", "current", "today", "news", "stock", "earnings",
                    "price", "quote", "close", "after-hours", "pre-market");

            bool needsReasoning =
                ContainsAny(topText,
                    "分析", "比較", "預測", "推論", "走勢", "建議",
                    "analyze", "compare", "predict", "forecast", "recommend");

            bool needsCode =
                ContainsAny(topText,
                    "程式", "程式碼", "bug", "debug", "class", "method", "code");

            var ordered = new List<string>();
            var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 重要：需要新資料時，search 永遠第一優先
            if (needsFreshFacts)
            {
                ordered.Add("search-capability");
                required.Add("search-capability");
            }

            if (hasAttachments)
                ordered.Add("file-capability");

            if (needsCode)
                ordered.Add("code-capability");

            // task planning 只是輔助，不應該搶在 search 前面
            ordered.Add("task-planning-capability");

            if (needsReasoning)
                ordered.Add("reasoning-capability");

            ordered = ordered
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return new AgentCapabilityExecutionPlan
            {
                OrderedCapabilityIds = ordered,
                RequiredCapabilityIds = required,
                RequiresFreshFacts = needsFreshFacts,
                Reason = needsFreshFacts
                    ? "Task requires fresh/current external facts; search-capability is required."
                    : "Task does not strictly require fresh facts."
            };
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword) &&
                    text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}
