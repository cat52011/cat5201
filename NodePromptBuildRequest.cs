namespace test
{
    public sealed class NodePromptBuildRequest
    {
        public NodeControl CurrentNode { get; init; } = null!;
        public string TopText { get; init; } = "";
        public NodeTaskMode TaskMode { get; init; } = NodeTaskMode.Chat;
        public NodeContextStrategy Strategy { get; init; } = NodeContextStrategy.Full;
    }
}