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

        // API 回傳的真實 token 用量。null = 該 provider 尚未接上真實 usage（呼叫端會退回估算）。
        public int? InputTokens { get; init; }
        public int? OutputTokens { get; init; }

        public IReadOnlyDictionary<string, string> Metadata { get; init; }
            = new Dictionary<string, string>();

        public static AiResponse Success(
            string text,
            string modelUsed,
            AiProviderKind providerUsed,
            string finishReason = "completed",
            IReadOnlyDictionary<string, string>? metadata = null,
            int? inputTokens = null,
            int? outputTokens = null)
        {
            return new AiResponse
            {
                IsSuccess = true,
                Text = text ?? "",
                ModelUsed = modelUsed ?? "",
                ProviderUsed = providerUsed,
                FinishReason = finishReason ?? "",
                Metadata = metadata ?? new Dictionary<string, string>(),
                InputTokens = inputTokens,
                OutputTokens = outputTokens
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