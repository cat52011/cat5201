using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class AgentRegistry
    {
        private static readonly IReadOnlyList<AgentDefinition> _all = new[]
        {
            new AgentDefinition
            {
                Id = "general-agent",
                Name = "General Agent",
                Role = AgentRole.General,
                DefaultModelId = AiModels.OpenAi_Gpt54,
                DefaultTaskMode = NodeTaskMode.Chat,
                SystemPrompt = "你是一個通用型節點代理，負責一般對話、整理與多用途任務。",
                AllowedModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46,
                    AiModels.Claude_Opus46
                },
                Capabilities =
                    AgentCapability.Chat |
                    AgentCapability.Summarize |
                    AgentCapability.Rewrite |
                    AgentCapability.Extract |
                    AgentCapability.Images |
                    AgentCapability.Files |
                    AgentCapability.LongContext |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite |
                    AgentCapability.FileTool |
                    AgentCapability.ImageTool,
                AllowDelegation = false,

                AllowedCapabilityIds = new[]
{
    "task-planning-capability",
    "search-capability",
    "file-capability",
    "image-capability",
    "reasoning-capability"
},
PreferredCapabilityIds = new[]
{
    "task-planning-capability",
    "file-capability",
    "reasoning-capability"
},
            },

            new AgentDefinition
            {
                Id = "research-agent",
                Name = "Research Agent",
                Role = AgentRole.Researcher,
                DefaultModelId = AiModels.Perplexity_Sonar,
                DefaultTaskMode = NodeTaskMode.Research,
                SystemPrompt = "你是一個研究型代理，負責查證、搜尋、比較、補充背景與整理資訊。",
                AllowedModelIds = new[]
                {
                    AiModels.Perplexity_Sonar,
                    AiModels.Perplexity_SonarDeepResearch,
                    AiModels.OpenAi_Gpt54
                },
                Capabilities =
                    AgentCapability.Research |
                    AgentCapability.Search |
                    AgentCapability.Files |
                    AgentCapability.LongContext |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite |
                    AgentCapability.Delegation |
                    AgentCapability.ToolUse |
                    AgentCapability.FileTool,
                AllowDelegation = true,

                AllowedCapabilityIds = new[]
{
    "task-planning-capability",
    "search-capability",
    "file-capability",
    "reasoning-capability"
},
PreferredCapabilityIds = new[]
{
    "task-planning-capability",
    "search-capability",
    "reasoning-capability"
},
            },

            new AgentDefinition
            {
                Id = "translation-agent",
                Name = "Translation Agent",
                Role = AgentRole.Translator,
                DefaultModelId = AiModels.OpenAi_Gpt54,
                DefaultTaskMode = NodeTaskMode.Translate,
                SystemPrompt = "你是一個翻譯型代理，負責忠實翻譯、保留原意、整理格式與對照輸出。",
                AllowedModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46,
                    AiModels.Claude_Opus46
                },
                Capabilities =
                    AgentCapability.Translate |
                    AgentCapability.Images |
                    AgentCapability.Files |
                    AgentCapability.LongContext |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite |
                    AgentCapability.FileTool |
                    AgentCapability.ImageTool,

                AllowedCapabilityIds = new[]
{
    "task-planning-capability",
    "file-capability",
    "image-capability",
    "reasoning-capability"
},
            },

            new AgentDefinition
            {
                Id = "code-agent",
                Name = "Code Agent",
                Role = AgentRole.Coder,
                DefaultModelId = AiModels.Claude_Opus46,
                DefaultTaskMode = NodeTaskMode.Code,
                SystemPrompt = "你是一個程式型代理，負責程式生成、除錯、架構修改與工程分析。",
                AllowedModelIds = new[]
                {
                    AiModels.Claude_Opus46,
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46
                },
                Capabilities =
                    AgentCapability.Code |
                    AgentCapability.Files |
                    AgentCapability.Search |
                    AgentCapability.LongContext |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite |
                    AgentCapability.Delegation |
                    AgentCapability.ToolUse |
                    AgentCapability.FileTool |
                    AgentCapability.CodeTool,
                AllowDelegation = true,

                AllowedCapabilityIds = new[]
{
    "task-planning-capability",
    "code-capability",
    "file-capability",
    "reasoning-capability"
},
PreferredCapabilityIds = new[]
{
    "task-planning-capability",
    "code-capability",
    "reasoning-capability"
},
            },

            new AgentDefinition
            {
                Id = "image-agent",
                Name = "Image Agent",
                Role = AgentRole.General,
                DefaultModelId = AiModels.OpenAi_Gpt54,
                DefaultTaskMode = NodeTaskMode.Chat,
                SystemPrompt = "你是一個圖片生成型代理，負責把使用者的描述交給圖片生成模型產圖，並用一句話確認任務。",
                AllowedModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46
                },
                Capabilities =
                    AgentCapability.Images |
                    AgentCapability.Files |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite |
                    AgentCapability.ImageTool |
                    AgentCapability.FileTool,
                AllowDelegation = false,

                AllowedCapabilityIds = new[]
{
    "image-capability",
    "reasoning-capability"
},
PreferredCapabilityIds = new[]
{
    "image-capability"
},
            }
        };

        public static IReadOnlyList<AgentDefinition> All => _all;

        public static AgentDefinition Default =>
            Find("general-agent") ?? _all.First();

        public static AgentDefinition? Find(string? id)
        {
            if (string.IsNullOrWhiteSpace(id))
                return null;

            return _all.FirstOrDefault(x =>
                string.Equals(x.Id, id.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        public static AgentDefinition Get(string? id)
        {
            return Find(id) ?? Default;
        }

        public static bool IsKnown(string? id)
        {
            return Find(id) != null;
        }
    }
}