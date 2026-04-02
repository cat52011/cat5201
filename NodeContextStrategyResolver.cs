namespace test
{
    public sealed class NodeContextStrategyResolver
    {
        private readonly AiServiceRouter _router;

        public NodeContextStrategyResolver(AiServiceRouter router)
        {
            _router = router;
        }

        public NodeContextStrategy Resolve(string model, NodeTaskMode taskMode)
        {
            // 先保持你目前完全相同的規則，不改功能
            if (_router.IsPerplexityDeepResearchModel(model))
                return NodeContextStrategy.Research;

            if (_router.IsPerplexitySonarModel(model))
                return NodeContextStrategy.CompactSearch;

            return NodeContextStrategy.Full;
        }
    }
}