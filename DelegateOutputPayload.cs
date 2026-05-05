namespace test
{
    public sealed class DelegateOutputPayload
    {
        public string FromAgentId { get; init; } = "";
        public string ToAgentId { get; init; } = "";
        public string Instruction { get; init; } = "";
        public string Output { get; init; } = "";
        public bool Success { get; init; }
        public string ActualModelId { get; init; } = "";
        public string ErrorMessage { get; init; } = "";
    }
}