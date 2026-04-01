using System;
using System.Collections.Generic;

namespace test
{
    public sealed class ExecutionLogEntry
    {
        public DateTime Timestamp { get; set; }

        public string RequestedModel { get; set; } = "";
        public string ActualModel { get; set; } = "";

        public NodeTaskMode TaskMode { get; set; }

        public string SelectionMode { get; set; } = ""; // Manual / Auto

        public string Resolver { get; set; } = "";

        public double Confidence { get; set; }

        public string Reason { get; set; } = "";

        public bool UsedFallback { get; set; }

        public string FallbackSummary { get; set; } = "";

        public List<string> FallbackChain { get; set; } = new();

        public bool Success { get; set; }

        public string ErrorMessage { get; set; } = "";

        public double DurationMs { get; set; }
    }
}