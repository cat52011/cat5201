using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class AgentCapabilityTraceFormatter
    {
        public static string BuildSummary(IReadOnlyList<AgentCapabilityTraceItem> items)
        {
            if (items == null || items.Count == 0)
                return "-";

            var visible = items
                .Where(ShouldDisplayInUi)
                .ToList();

            if (visible.Count == 0)
                return "無有效 capability 觸發";

            var parts = visible
                .Select(x =>
                {
                    string state = x.Handled
                        ? "handled"
                        : x.AugmentedPrompt
                            ? "augment"
                            : x.Success
                                ? "pass"
                                : "fail";

                    return $"{x.CapabilityId}({state})";
                })
                .ToList();

            return parts.Count == 0 ? "-" : string.Join(" | ", parts);
        }

        public static IReadOnlyList<string> BuildDetailLines(IReadOnlyList<AgentCapabilityTraceItem> items)
        {
            if (items == null || items.Count == 0)
                return new List<string>();

            var visible = items
                .Where(ShouldDisplayInUi)
                .ToList();

            if (visible.Count == 0)
                return new List<string> { "本次沒有實際觸發的 capability。" };

            return visible.Select(x =>
            {
                if (!x.Success)
                {
                    return
                        $"Capability: {x.CapabilityId} / Agent: {x.AgentId} / " +
                        $"CanHandle: {x.CanHandle} / Executed: {x.Executed} / Error: {x.ErrorMessage}";
                }

                return
                    $"Capability: {x.CapabilityId} / Agent: {x.AgentId} / " +
                    $"CanHandle: {x.CanHandle} / Executed: {x.Executed} / " +
                    $"Handled: {x.Handled} / Augmented: {x.AugmentedPrompt} / " +
                    $"Summary: {x.Summary}";
            }).ToList();
        }

        private static bool ShouldDisplayInUi(AgentCapabilityTraceItem item)
        {
            if (item == null)
                return false;

            // 只顯示真正有意義的項目：
            // 1. 有執行過
            // 2. 有直接 handled
            // 3. 有 prompt augment
            // 4. 有錯誤
            if (item.Executed)
                return true;

            if (item.Handled)
                return true;

            if (item.AugmentedPrompt)
                return true;

            if (!item.Success)
                return true;

            // 像 skipped / agent capability not allowed 這種先不顯示
            return false;
        }
    }
}