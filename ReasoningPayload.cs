using System.Collections.Generic;

namespace test
{
    public sealed class ReasoningPayload
    {
        public string ReasoningType { get; init; } = "";
        // predict / compare / analyze / recommend

        public string Basis { get; init; } = "";

        public IReadOnlyList<string> Inferences { get; init; }
            = new List<string>();

        public IReadOnlyList<string> Uncertainties { get; init; }
            = new List<string>();

        public string OutputGuidance { get; init; } = "";
    }
}