using System.Collections.Generic;

namespace test
{
    public sealed class NodeDecisionViewData
    {
        public string Status { get; init; } = "-";
        public string Mode { get; init; } = "-";
        public string Resolver { get; init; } = "-";
        public string Agent { get; init; } = "-";
        public string Model { get; init; } = "-";
        public string TaskSummary { get; init; } = "-";
        public string Reason { get; init; } = "-";
        public string Keywords { get; init; } = "-";
        public string Extra { get; init; } = "-";

        public string CapabilitySummary { get; init; } = "-";
        public IReadOnlyList<string> CapabilityDetails { get; init; }
            = new List<string>();

        public string DelegationSummary { get; init; } = "-";
        public IReadOnlyList<string> DelegationDetails { get; init; }
            = new List<string>();

        public bool CapabilityAdjusted { get; init; }
        public bool RuntimeFallbackUsed { get; init; }
        public bool ApiFallbackUsed { get; init; }

        public IReadOnlyList<NodeDecisionStepViewData> Steps { get; init; }
            = new List<NodeDecisionStepViewData>();
    }
}