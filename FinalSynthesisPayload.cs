namespace test
{
    public sealed class FinalSynthesisPayload
    {
        public string SynthesizerAgentId { get; init; } = "";

        public string ModelId { get; init; } = "";

        public string Output { get; init; } = "";

        public bool Success { get; init; }

        public string ErrorMessage { get; init; } = "";
    }
}