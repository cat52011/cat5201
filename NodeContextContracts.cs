namespace test
{
    public enum NodeContextStrategy
    {
        Full = 0,
        CompactSearch = 1,
        Research = 2
    }

    public sealed class NodeContextBundle
    {
        public string UpstreamContext { get; set; } = "";
        public string DownstreamContext { get; set; } = "";
        public string BranchSummaryContext { get; set; } = "";
        public string AttachmentHint { get; set; } = "";
    }
}