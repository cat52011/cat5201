using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class TaskPlanningCapability : IAgentCapability
    {
        public string Id => "task-planning-capability";

        public AgentCapability RequiredAgentCapability => AgentCapability.None;

        public bool CanHandle(AgentExecutionContext context)
        {
            if (context == null)
                return false;

            string text = context.TopText ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return IsCompoundTask(text);
        }

        public Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct)
        {
            string text = context.TopText ?? "";

            var steps = BuildSteps(text);

            if (steps.Count == 0)
                return Task.FromResult(AgentCapabilityResult.NotHandled());

            var payload = new TaskDecompositionPayload
            {
                Steps = steps,
                Summary = BuildSummary(steps)
            };

            return Task.FromResult(
                AgentCapabilityResult.WithData("task_plan", payload));
        }

        private static bool IsCompoundTask(string text)
        {
            bool hasSearch =
                ContainsAny(text, "查", "查一下", "查詢", "搜尋", "最新", "財報", "新聞", "research", "search", "latest");

            bool hasReasoning =
                ContainsAny(text, "分析", "比較", "預測", "推論", "走勢", "未來", "compare", "predict", "forecast", "analyze");

            bool hasFile =
                ContainsAny(text, "附件", "PDF", "檔案", "文件", "菜單", "翻譯", "摘要", "整理");

            bool hasCode =
                ContainsAny(text, "程式", "程式碼", "code", "bug", "debug", "修正", "null", "class", "method");

            int count = 0;
            if (hasSearch) count++;
            if (hasReasoning) count++;
            if (hasFile) count++;
            if (hasCode) count++;

            return count >= 2;
        }

        private static List<TaskDecompositionStep> BuildSteps(string text)
        {
            var steps = new List<TaskDecompositionStep>();
            int order = 1;

            bool hasSearch =
                ContainsAny(text, "查", "查一下", "查詢", "搜尋", "最新", "財報", "新聞", "research", "search", "latest");

            bool hasSummarize =
                ContainsAny(text, "整理", "重點", "摘要", "財報重點", "summary", "summarize");

            bool hasCompare =
                ContainsAny(text, "比較", "誰", "哪個", "vs", "VS", "compare");

            bool hasPredict =
                ContainsAny(text, "預測", "推論", "未來", "走勢", "forecast", "predict");

            bool hasFile =
                ContainsAny(text, "附件", "PDF", "檔案", "文件", "菜單");

            bool hasCode =
                ContainsAny(text, "程式", "程式碼", "code", "bug", "debug", "修正", "null", "class", "method");

            if (hasSearch)
            {
                steps.Add(new TaskDecompositionStep
                {
                    Order = order++,
                    StepType = "search",
                    Goal = "取得目前任務需要的最新或外部資料。",
                    RequiredInput = "使用者原始問題與關鍵查詢詞。",
                    OutputExpectation = "產生可被後續整理與推論使用的 search_summary。"
                });
            }

            if (hasFile)
            {
                steps.Add(new TaskDecompositionStep
                {
                    Order = order++,
                    StepType = "file",
                    Goal = "理解附件內容與附件類型。",
                    RequiredInput = "目前節點附件。",
                    OutputExpectation = "產生 file_summary，供後續摘要、翻譯或抽取使用。"
                });
            }

            if (hasCode)
            {
                steps.Add(new TaskDecompositionStep
                {
                    Order = order++,
                    StepType = "code",
                    Goal = "分析程式任務類型、語言、錯誤或修改目標。",
                    RequiredInput = "使用者程式需求與可能的附件程式碼。",
                    OutputExpectation = "產生 code_analysis，指示後續回答應採取的程式處理策略。"
                });
            }

            if (hasSummarize)
            {
                steps.Add(new TaskDecompositionStep
                {
                    Order = order++,
                    StepType = "summarize",
                    Goal = "整理已取得資料的重點。",
                    RequiredInput = "search_summary、file_summary 或 code_analysis。",
                    OutputExpectation = "輸出精簡、可回答主問題的重點摘要。"
                });
            }

            if (hasCompare)
            {
                steps.Add(new TaskDecompositionStep
                {
                    Order = order++,
                    StepType = "compare",
                    Goal = "比較多個對象的差異、優勢或結果。",
                    RequiredInput = "已取得的結構化資料與摘要。",
                    OutputExpectation = "給出比較表或明確比較結論；若資料不足，需明確說明。"
                });
            }

            if (hasPredict)
            {
                steps.Add(new TaskDecompositionStep
                {
                    Order = order++,
                    StepType = "predict",
                    Goal = "基於已知資料做有限度推論或短期預測。",
                    RequiredInput = "search_summary 與已知事實。",
                    OutputExpectation = "輸出有依據的推論；不可捏造資料，需標明不確定性。"
                });
            }

            steps.Add(new TaskDecompositionStep
            {
                Order = order,
                StepType = "answer",
                Goal = "整合前面步驟結果，回答使用者原始問題。",
                RequiredInput = "所有 capability data 與目前節點內容。",
                OutputExpectation = "輸出最終答案，不暴露內部 task_plan / memory / delegate 標記。"
            });

            return steps;
        }

        private static string BuildSummary(IReadOnlyList<TaskDecompositionStep> steps)
        {
            if (steps == null || steps.Count == 0)
                return "此任務不需要拆解。";

            return "此任務已拆解為：" +
                   string.Join(" → ", steps.Select(x => x.StepType));
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