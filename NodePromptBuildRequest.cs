namespace test
{
    public sealed class NodePromptBuildRequest
    {
        public NodeControl CurrentNode { get; init; } = null!;
        public string TopText { get; init; } = "";
        public NodeTaskMode TaskMode { get; init; } = NodeTaskMode.Chat;
        public NodeContextStrategy Strategy { get; init; } = NodeContextStrategy.Full;

        public string MemoryBlock { get; init; } = "";

        // 使用者偏好區塊（最高優先），放在 prompt 最上方。
        public string PreferenceBlock { get; init; } = "";
    }
}