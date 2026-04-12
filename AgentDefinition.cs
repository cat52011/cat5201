using System.Collections.Generic;

namespace test
{
    public sealed class AgentDefinition
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";

        public AgentRole Role { get; init; } = AgentRole.General;

        // 相容你目前系統：先保留 DefaultModelId / DefaultTaskMode
        public string DefaultModelId { get; init; } = AiModels.OpenAi_Gpt54;
        public NodeTaskMode DefaultTaskMode { get; init; } = NodeTaskMode.Chat;

        // Agent 系統提示
        public string SystemPrompt { get; init; } = "";

        // 允許的模型清單（第一版先給 routing / UI 用）
        public IReadOnlyList<string> AllowedModelIds { get; init; } = new List<string>();

        public AgentCapability Capabilities { get; init; } =
            AgentCapability.Chat |
            AgentCapability.MemoryRead |
            AgentCapability.MemoryWrite;

        public AgentMemoryPolicy MemoryPolicy { get; init; } = AgentMemoryPolicy.Default;

        public bool AllowDelegation { get; init; }
        public bool IsSystemAgent { get; init; }

        public bool Supports(NodeTaskMode mode)
        {
            return mode switch
            {
                NodeTaskMode.Chat => Capabilities.HasFlag(AgentCapability.Chat),
                NodeTaskMode.Research => Capabilities.HasFlag(AgentCapability.Research),
                NodeTaskMode.Translate => Capabilities.HasFlag(AgentCapability.Translate),
                NodeTaskMode.Summarize => Capabilities.HasFlag(AgentCapability.Summarize),
                NodeTaskMode.Rewrite => Capabilities.HasFlag(AgentCapability.Rewrite),
                NodeTaskMode.Extract => Capabilities.HasFlag(AgentCapability.Extract),
                NodeTaskMode.Code => Capabilities.HasFlag(AgentCapability.Code),
                _ => false
            };
        }
    }
}