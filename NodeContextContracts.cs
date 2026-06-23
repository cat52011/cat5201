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

        // 上游鏈「設定/指示」摘錄（祖先 Top，最接近本節點者在前）。
        // 用途：持續性設定（語言/格式/語氣/風格）要延續到下游，且優先級高於全域偏好；多條時最近的上游為準。
        public string ChainDirectiveContext { get; set; } = "";
    }
}