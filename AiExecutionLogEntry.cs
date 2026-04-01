using System;

namespace test
{
    public sealed class AiExecutionLogEntry
    {
        public string NodeId { get; init; } = "";

        public DateTime StartedAtUtc { get; init; }
        public DateTime EndedAtUtc { get; init; }

        public long DurationMs { get; init; }

        public string SelectionMode { get; init; } = "";
        public string Resolver { get; init; } = "";

        public string RequestedModelId { get; init; } = "";
        public string PlannedModelId { get; init; } = "";
        public string ActualModelId { get; init; } = "";

        public string TaskMode { get; init; } = "";
        public double Confidence { get; init; }

        public bool CapabilityAdjusted { get; init; }
        public string CapabilityReason { get; init; } = "";

        public bool RuntimeFallbackUsed { get; init; }
        public string RuntimeFallbackSummary { get; init; } = "";

        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";
    }
}