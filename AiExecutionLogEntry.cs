using System;
using System.Collections.Generic;

namespace test
{
    public sealed class AiExecutionLogEntry
    {
        public string RequestedAgentId { get; init; } = "";
        public string ActualAgentId { get; init; } = "";    
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

        public string ResolverReason { get; init; } = "";
        public IReadOnlyList<string> ResolverKeywords { get; init; }
            = Array.Empty<string>();

        public bool CapabilityAdjusted { get; init; }
        public string CapabilityReason { get; init; } = "";

        public string CapabilityRequestedModelId { get; init; } = "";
        public string CapabilityResolvedModelId { get; init; } = "";
        public string CapabilityRequired { get; init; } = "";
        public string CapabilityMissing { get; init; } = "";
        public bool CapabilityStreamingAdjusted { get; init; }

        public bool RuntimeFallbackUsed { get; init; }
        public string RuntimeFallbackSummary { get; init; } = "";

        public bool Success { get; init; }
        public string ErrorMessage { get; init; } = "";

        public IReadOnlyList<AiFallbackAttempt> FallbackAttempts { get; init; }
            = Array.Empty<AiFallbackAttempt>();
    }
}