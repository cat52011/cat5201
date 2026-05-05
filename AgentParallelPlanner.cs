using System;
using System.Collections.Generic;

namespace test
{
    public sealed class AgentParallelPlanner
    {
        public IReadOnlyList<AgentParallelTask> Plan(
            AgentDefinition rootAgent,
            string input,
            NodeTaskMode taskMode)
        {
            input ??= "";

            var tasks = new List<AgentParallelTask>();

            bool wantsSearch = ContainsAny(
                input,
                "查", "查詢", "搜尋", "最新", "財報", "新聞",
                "research", "search", "latest");

            bool wantsReasoning = ContainsAny(
                input,
                "預測", "推論", "走勢", "比較", "分析", "建議",
                "predict", "forecast", "compare", "analyze", "recommend");

            bool wantsCode = ContainsAny(
                input,
                "程式", "程式碼", "code", "bug", "debug", "修正",
                "class", "method", "null");

            bool wantsFile = ContainsAny(
                input,
                "附件", "PDF", "檔案", "文件", "菜單", "翻譯", "摘要", "整理");

            if (wantsSearch)
            {
                tasks.Add(new AgentParallelTask
                {
                    AgentId = "research-agent",
                    Purpose = "search",
                    Instruction = "請專注取得外部資料、最新資訊、財報、新聞或查證結果，並輸出可供其他代理使用的重點。"
                });
            }

            if (wantsCode)
            {
                tasks.Add(new AgentParallelTask
                {
                    AgentId = "code-agent",
                    Purpose = "code",
                    Instruction = "請專注分析程式、錯誤、架構或修改需求，輸出可供最終整合使用的程式分析。"
                });
            }

            if (wantsFile)
            {
                tasks.Add(new AgentParallelTask
                {
                    AgentId = "translation-agent",
                    Purpose = "file_or_translation",
                    Instruction = "請專注理解附件、文件、翻譯或摘要需求，輸出可供最終整合使用的附件/翻譯重點。"
                });
            }

            if (wantsReasoning)
            {
                tasks.Add(new AgentParallelTask
                {
                    AgentId = "general-agent",
                    Purpose = "reasoning",
                    Instruction = "請專注基於已知資料做分析、比較、推論或預測，明確標示不確定性。"
                });
            }

            // 若只有一個任務，不需要 parallel，避免過度複雜化
            if (tasks.Count <= 1)
                return Array.Empty<AgentParallelTask>();

            return tasks;
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