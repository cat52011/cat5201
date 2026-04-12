using System.Collections.Generic;

namespace test
{
    public sealed class AgentDelegationPlanner
    {
        public IReadOnlyList<AgentDelegationRequest> Plan(
            AgentDefinition agent,
            string input,
            NodeTaskMode taskMode)
        {
            var list = new List<AgentDelegationRequest>();

            if (agent == null || !agent.AllowDelegation)
                return list;

            input ??= "";

            // research-agent：
            // 當輸入明顯是「整理 / 分析 / 彙整」時，
            // 先讓 general-agent 做一份通用整理，再回來由 research-agent 完成
            if (agent.Id == "research-agent")
            {
                if (input.Contains("整理") ||
                    input.Contains("分析") ||
                    input.Contains("比較") ||
                    input.Contains("彙整"))
                {
                    list.Add(new AgentDelegationRequest
                    {
                        TargetAgentId = "general-agent",
                        Instruction = input
                    });
                }
            }

            // code-agent：
            // 當輸入包含架構分析 / 系統分析 / 設計說明時，
            // 先讓 research-agent 做脈絡整理，再由 code-agent 產出工程結果
            if (agent.Id == "code-agent")
            {
                if (input.Contains("架構") ||
                    input.Contains("分析") ||
                    input.Contains("設計") ||
                    input.Contains("流程"))
                {
                    list.Add(new AgentDelegationRequest
                    {
                        TargetAgentId = "research-agent",
                        Instruction = input
                    });
                }
            }

            return list;
        }
    }
}