using System;
using System.Collections.Generic;

namespace test
{
    public sealed class WorkflowPlanPayload
    {
        public string WorkflowId { get; init; } = Guid.NewGuid().ToString("N");

        public string Status { get; init; } = "draft";

        public string PipelineId { get; init; } = "";

        public OrchestrationTaskType TaskType { get; init; } = OrchestrationTaskType.Chat;

        public string SourceNodeId { get; init; } = "";

        public string UserInputPreview { get; init; } = "";

        public bool CreatesCanvasNodes { get; init; }

        public bool ReplaySupported { get; init; }

        public bool RerunSupported { get; init; }

        public bool ResumeSupported { get; init; }

        public IReadOnlyList<WorkflowNodePayload> Nodes { get; init; } = Array.Empty<WorkflowNodePayload>();

        public IReadOnlyList<WorkflowEdgePayload> Edges { get; init; } = Array.Empty<WorkflowEdgePayload>();

        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    public sealed class WorkflowNodePayload
    {
        public string Id { get; init; } = "";

        public string Label { get; init; } = "";

        public string TaskType { get; init; } = "";

        public string AgentId { get; init; } = "";

        public string ModelId { get; init; } = "";

        public string CapabilityId { get; init; } = "";

        public string InputPreview { get; init; } = "";

        public string OutputPreview { get; init; } = "";

        public string Status { get; init; } = "planned";
    }

    public sealed class WorkflowEdgePayload
    {
        public string FromNodeId { get; init; } = "";

        public string ToNodeId { get; init; } = "";

        public string Kind { get; init; } = "data";
    }
}
