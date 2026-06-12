using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public sealed class OrchestrationPlanPayload
    {
        // pending / running / success / failed / partial（由 OrchestrationStateMachine 維護）
        public string Status { get; set; } = "pending";

        public OrchestrationTaskType TaskType { get; init; } = OrchestrationTaskType.Chat;

        public string PipelineId { get; init; } = "chat";

        public string TaskMode { get; init; } = "";

        public string RequestedAgentId { get; init; } = "";

        public string RuntimeAgentId { get; init; } = "";

        public string ModelId { get; init; } = "";

        public bool AutoMode { get; init; }

        public bool HasAttachments { get; init; }

        public bool RequiresFreshFacts { get; init; }

        public IReadOnlyList<string> CapabilityOrder { get; init; } = Array.Empty<string>();

        public IReadOnlyList<string> RequiredCapabilities { get; init; } = Array.Empty<string>();

        public IReadOnlyList<OrchestrationStagePayload> Stages { get; init; } = Array.Empty<OrchestrationStagePayload>();

        public string Reason { get; init; } = "";

        public bool IsCapabilityRequired(string capabilityId)
        {
            if (string.IsNullOrWhiteSpace(capabilityId))
                return false;

            return RequiredCapabilities != null &&
                   RequiredCapabilities.Any(x =>
                       string.Equals(x, capabilityId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public sealed class OrchestrationStagePayload
    {
        public int Order { get; init; }

        public string Id { get; init; } = "";

        public string Label { get; init; } = "";

        // pending / running / success / failed / partial / skipped（由 OrchestrationStateMachine 維護）
        public string Status { get; set; } = "pending";

        // 補充說明：失敗原因、跳過原因、產出摘要等
        public string Detail { get; set; } = "";

        public string Owner { get; init; } = "";
    }
}
