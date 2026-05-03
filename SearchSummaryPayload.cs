using System.Collections.Generic;

namespace test
{
    public sealed class SearchSummaryPayload
    {
        public string Query { get; init; } = "";

        public string Summary { get; init; } = "";

        public IReadOnlyList<SearchSummaryItem> Items { get; init; }
            = new List<SearchSummaryItem>();
    }

    public sealed class SearchSummaryItem
    {
        public string Title { get; init; } = "";

        public string KeyPoint { get; init; } = "";

        public string Source { get; init; } = "";

        public string Date { get; init; } = "";
    }
}