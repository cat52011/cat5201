namespace test
{
    public sealed class NodeModelSelectionService
    {
        public string ResolveRuleAutoModel(
            NodeTaskMode taskMode,
            string? currentSelectedModel)
        {
            return AiModelHelper.NormalizeNodeModel(
                NodeTaskRoutingRegistry.RecommendModel(taskMode, currentSelectedModel));
        }
    }
}