using System;
using System.Collections.Generic;

namespace test
{
    public sealed class AgentDefinition
    {
        public string Id { get; init; } = "";
        public string Name { get; init; } = "";

        public AgentRole Role { get; init; } = AgentRole.General;

        public string DefaultModelId { get; init; } = AiModels.OpenAi_Gpt54;
        public NodeTaskMode DefaultTaskMode { get; init; } = NodeTaskMode.Chat;

        public string SystemPrompt { get; init; } = "";

        public IReadOnlyList<string> AllowedModelIds { get; init; } = Array.Empty<string>();

        public AgentCapability Capabilities { get; init; } =
            AgentCapability.Chat |
            AgentCapability.MemoryRead |
            AgentCapability.MemoryWrite;

        public AgentMemoryPolicy MemoryPolicy { get; init; } = AgentMemoryPolicy.Default;

        public bool AllowDelegation { get; init; }
        public bool IsSystemAgent { get; init; }

        // ===== Phase 2: Capability Policy =====
        public IReadOnlyList<string> AllowedCapabilityIds { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> PreferredCapabilityIds { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> BlockedCapabilityIds { get; init; } = Array.Empty<string>();

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

        public bool SupportsCapability(string capabilityId)
        {
            return IsCapabilityAllowed(capabilityId);
        }

        public bool IsCapabilityBlocked(string capabilityId)
        {
            if (string.IsNullOrWhiteSpace(capabilityId) ||
                BlockedCapabilityIds == null ||
                BlockedCapabilityIds.Count == 0)
            {
                return false;
            }

            foreach (var id in BlockedCapabilityIds)
            {
                if (string.Equals(id, capabilityId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public bool IsCapabilityAllowed(string capabilityId)
        {
            if (string.IsNullOrWhiteSpace(capabilityId))
                return false;

            if (IsCapabilityBlocked(capabilityId))
                return false;

            // 沒有限制清單 = 預設允許
            if (AllowedCapabilityIds == null || AllowedCapabilityIds.Count == 0)
                return true;

            foreach (var id in AllowedCapabilityIds)
            {
                if (string.Equals(id, capabilityId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        public bool IsPreferredCapability(string capabilityId)
        {
            if (string.IsNullOrWhiteSpace(capabilityId) ||
                PreferredCapabilityIds == null ||
                PreferredCapabilityIds.Count == 0)
            {
                return false;
            }

            foreach (var id in PreferredCapabilityIds)
            {
                if (string.Equals(id, capabilityId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }
    }
}