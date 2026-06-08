using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class ReasoningCapability : IAgentCapability
    {
        public string Id => "reasoning-capability";

        public AgentCapability RequiredAgentCapability => AgentCapability.None;

        public bool CanHandle(AgentExecutionContext context)
        {
            if (context == null)
                return false;

            string text = context.TopText ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(
                text,
                "預測", "推論", "走勢", "未來", "比較", "分析", "建議",
                "predict", "forecast", "compare", "analyze", "recommend");
        }

        public Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct)
        {
            string text = context.TopText ?? "";

            var payload = new ReasoningPayload
            {
                ReasoningType = ResolveReasoningType(text),
                Basis = "請只基於目前任務、Search Summary、File Summary、Code Analysis 與 Task Plan 中已提供的資料推論。",
                Inferences = BuildInferences(text),
                Uncertainties = BuildUncertainties(text),
                OutputGuidance = BuildOutputGuidance(text)
            };

            return Task.FromResult(
                AgentCapabilityResult.WithData("reasoning_analysis", payload));
        }

        private static string ResolveReasoningType(string text)
        {
            if (ContainsAny(text, "預測", "走勢", "未來", "predict", "forecast"))
                return "predict";

            if (ContainsAny(text, "比較", "誰", "哪個", "vs", "VS", "compare"))
                return "compare";

            if (ContainsAny(text, "建議", "recommend"))
                return "recommend";

            return "analyze";
        }

        private static IReadOnlyList<string> BuildInferences(string text)
        {
            var list = new List<string>();

            if (ContainsAny(text, "預測", "走勢", "未來", "predict", "forecast"))
            {
                list.Add("先整理已知事實，再做有限度短期推論。");
                list.Add("推論必須明確區分『已知資料』與『可能走勢』。");
                list.Add("不可給出保證式結論，也不可捏造未提供的價格區間。");
            }

            if (ContainsAny(text, "比較", "誰", "哪個", "vs", "VS", "compare"))
            {
                list.Add("比較時必須說明比較基準，例如漲幅、估值、財報、動能或風險。");
                list.Add("若資料不足以判定贏家，必須明確說資料不足。");
            }

            if (ContainsAny(text, "建議", "recommend"))
            {
                list.Add("建議必須附帶風險與不確定性。");
                list.Add("不得把推論包裝成確定事實。");
            }

            if (list.Count == 0)
                list.Add("根據已知資料做保守分析，資料不足時明確說明。");

            return list;
        }

        private static IReadOnlyList<string> BuildUncertainties(string text)
        {
            return new[]
            {
                "短期市場價格可能受大盤、利率、財報解讀、地緣政治與突發新聞影響。",
                "若 Search Summary 沒有提供明確數據，不可自行補數字。",
                "若使用者要求預測，輸出應是情境推論，不是確定預言。"
            };
        }

        private static string BuildOutputGuidance(string text)
        {
            return
                "回答時必須清楚區分事實資料與合理推論；若 final synthesizer 已指定格式，必須服從 final synthesizer 格式。" +
                "不得輸出內部標記，不得輸出 citation marker，例如 [1][2][3]。";
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
    }
}
