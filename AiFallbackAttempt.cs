namespace test
{
    public sealed class AiFallbackAttempt
    {
        public int AttemptIndex { get; init; }

        public string ModelId { get; init; } = "";

        public string Reason { get; init; } = "";

        public bool Success { get; init; }

        public string ErrorMessage { get; init; } = "";
    }
}