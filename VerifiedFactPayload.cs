using System.Collections.Generic;

namespace test
{
    public sealed class VerifiedFactPayload
    {
        public string Query { get; init; } = "";

        public IReadOnlyList<VerifiedFactItem> Facts { get; init; }
            = new List<VerifiedFactItem>();

        public string Summary { get; init; } = "";
    }

    public sealed class VerifiedFactItem
    {
        public string Subject { get; init; } = "";

        public string FactType { get; init; } = "";
        // quote / earnings / news / date / general

        public string Value { get; init; } = "";

        public string Unit { get; init; } = ""; 

        public string AsOf { get; init; } = "";

        public string SourceTitle { get; init; } = "";

        public string SourceUrl { get; init; } = "";

        public string Confidence { get; init; } = "medium";
    }
}