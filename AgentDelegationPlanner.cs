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
            if (agent == null)
                return Array.Empty<AgentDelegationRequest>();

            if (!agent.AllowDelegation)
                return Array.Empty<AgentDelegationRequest>();

            input ??= "";

            var plans = new List<AgentDelegationRequest>();

            // Research → General synthesis
            if (string.Equals(
                agent.Id,
                "research-agent",
                StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsAny(
                    input,
                    "預測", "推論", "比較", "分析",
                    "predict", "forecast", "compare", "analyze"))
                {
                    plans.Add(new AgentDelegationRequest
                    {
                        TargetAgentId = "general-agent",
                        Instruction =
                            "請基於目前 workspace 中已有的 search / reasoning data，做最後整合與人類可讀輸出。"
                    });
                }
            }

            // Code → General explanation
            if (string.Equals(
                agent.Id,
                "code-agent",
                StringComparison.OrdinalIgnoreCase))
            {
                if (ContainsAny(
                    input,
                    "解釋", "說明", "explain"))
                {
                    plans.Add(new AgentDelegationRequest
                    {
                        TargetAgentId = "general-agent",
                        Instruction =
                            "請把目前 workspace 中的程式分析結果，整理成人類易理解的說明。"
                    });
                }
            }

            return plans;
        }

        private static bool ContainsAny(
    string text,
    params string[] keywords)
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