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
                    AgentCapability.MemoryWrite
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
                    AgentCapability.LongContext |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite |
                    AgentCapability.Delegation,
                AllowDelegation = true
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
                    AgentCapability.MemoryWrite
            },

            new AgentDefinition
            {
                Id = "writer-agent",
                Name = "Writer Agent",
                Role = AgentRole.Writer,
                DefaultModelId = AiModels.Claude_Sonnet46,
                DefaultTaskMode = NodeTaskMode.Rewrite,
                SystemPrompt = "你是一個寫作型代理，負責改寫、潤稿、重組結構與改善可讀性。",
                AllowedModelIds = new[]
                {
                    AiModels.Claude_Sonnet46,
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Opus46
                },
                Capabilities =
                    AgentCapability.Rewrite |
                    AgentCapability.Summarize |
                    AgentCapability.LongContext |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite
            },

            new AgentDefinition
            {
                Id = "extract-agent",
                Name = "Extract Agent",
                Role = AgentRole.Extractor,
                DefaultModelId = AiModels.OpenAi_Gpt54,
                DefaultTaskMode = NodeTaskMode.Extract,
                SystemPrompt = "你是一個擷取型代理，負責抽取欄位、結構化資訊與整理重點資料。",
                AllowedModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46
                },
                Capabilities =
                    AgentCapability.Extract |
                    AgentCapability.Files |
                    AgentCapability.Images |
                    AgentCapability.LongContext |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite
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
                    AgentCapability.LongContext |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite |
                    AgentCapability.Delegation,
                AllowDelegation = true
            },

            new AgentDefinition
            {
                Id = "coordinator-agent",
                Name = "Coordinator Agent",
                Role = AgentRole.Coordinator,
                DefaultModelId = AiModels.OpenAi_Gpt54,
                DefaultTaskMode = NodeTaskMode.Chat,
                SystemPrompt = "你是一個協調型代理，負責任務拆分、委派、彙整與多代理協作。",
                AllowedModelIds = new[]
                {
                    AiModels.OpenAi_Gpt54,
                    AiModels.Claude_Sonnet46
                },
                Capabilities =
                    AgentCapability.Chat |
                    AgentCapability.Research |
                    AgentCapability.Summarize |
                    AgentCapability.Delegation |
                    AgentCapability.ToolUse |
                    AgentCapability.MemoryRead |
                    AgentCapability.MemoryWrite,
                AllowDelegation = true,
                IsSystemAgent = true
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