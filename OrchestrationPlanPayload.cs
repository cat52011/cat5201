using System;
using System.Collections.Generic;

namespace test
{
    public sealed class OrchestrationPlanPayload
    {
        public string Status { get; init; } = "planned";

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
    }

    public sealed class OrchestrationStagePayload
    {
        public int Order { get; init; }

        public string Id { get; init; } = "";

        public string Label { get; init; } = "";

        public string Status { get; init; } = "planned";

        public string Owner { get; init; } = "";
    }
}
