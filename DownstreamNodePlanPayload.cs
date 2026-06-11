using System;
using System.Collections.Generic;

namespace test
{
    public sealed class DownstreamNodePlanPayload
    {
        public string PlanId { get; init; } = Guid.NewGuid().ToString("N");

        public string Status { get; init; } = "proposal";

        public string PipelineId { get; init; } = "";

        public OrchestrationTaskType TaskType { get; init; } = OrchestrationTaskType.Chat;

        public string SourceNodeId { get; init; } = "";

        public bool CreatesCanvasNodes { get; init; }

        public IReadOnlyList<DownstreamNodeProposalPayload> ProposedNodes { get; init; }
            = Array.Empty<DownstreamNodeProposalPayload>();

        public IReadOnlyList<DownstreamNodeEdgeProposalPayload> ProposedEdges { get; init; }
            = Array.Empty<DownstreamNodeEdgeProposalPayload>();

        public IReadOnlyList<string> Notes { get; init; } = Array.Empty<string>();
    }

    public sealed class DownstreamNodeProposalPayload
    {
        public string Id { get; init; } = "";

        public string Label { get; init; } = "";

        public string AgentId { get; init; } = "";

        public string CapabilityId { get; init; } = "";

        public string InputSource { get; init; } = "";

        public string ExpectedOutput { get; init; } = "";

        public string Status { get; init; } = "proposed";
    }

    public sealed class DownstreamNodeEdgeProposalPayload
    {
        public string FromNodeId { get; init; } = "";

        public string ToNodeId { get; init; } = "";

        public string Kind { get; init; } = "proposed_data";
    }
}
