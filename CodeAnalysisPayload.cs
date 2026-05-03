using System.Collections.Generic;

namespace test
{
    public sealed class CodeAnalysisPayload
    {
        public string RequestType { get; init; } = "";
        public string Language { get; init; } = "";
        public string UserGoal { get; init; } = "";

        public IReadOnlyList<string> DetectedSignals { get; init; }
            = new List<string>();

        public IReadOnlyList<string> RequiredActions { get; init; }
            = new List<string>();

        public string Guidance { get; init; } = "";
    }
}