using System.Collections.Generic;

namespace test
{
    public sealed class AiResponse
    {
        public bool IsSuccess { get; init; } = true;
        public string Text { get; init; } = "";

        public string ModelUsed { get; init; } = "";
        public AiProviderKind ProviderUsed { get; init; } = AiProviderKind.Unknown;

        public string FinishReason { get; init; } = "";
        public string ErrorMessage { get; init; } = "";

        public IReadOnlyDictionary<string, string> Metadata { get; init; }
            = new Dictionary<string, string>();

        public static AiResponse Success(
            string text,
            string modelUsed,
            AiProviderKind providerUsed,
            string finishReason = "completed",
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiResponse
            {
                IsSuccess = true,
                Text = text ?? "",
                ModelUsed = modelUsed ?? "",
                ProviderUsed = providerUsed,
                FinishReason = finishReason ?? "",
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }

        public static AiResponse Failure(
            string modelUsed,
            AiProviderKind providerUsed,
            string errorMessage,
            string finishReason = "error",
            IReadOnlyDictionary<string, string>? metadata = null)
        {
            return new AiResponse
            {
                IsSuccess = false,
                Text = "",
                ModelUsed = modelUsed ?? "",
                ProviderUsed = providerUsed,
                FinishReason = finishReason ?? "error",
                ErrorMessage = errorMessage ?? "",
                Metadata = metadata ?? new Dictionary<string, string>()
            };
        }
    }
}