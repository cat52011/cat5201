using System.Collections.Generic;

namespace test
{
    public sealed class AiRequest
    {
        public string ModelId { get; init; } = "";
        public string SystemPrompt { get; init; } = "";
        public string UserPrompt { get; init; } = "";

        public NodeTaskMode TaskMode { get; init; } = NodeTaskMode.Chat;

        public IReadOnlyList<AiAttachment> Attachments { get; init; } = new List<AiAttachment>();

        public bool UseStreaming { get; init; }
        public int MaxOutputTokens { get; init; } = 8000;

        public IReadOnlyDictionary<string, string> Metadata { get; init; }
            = new Dictionary<string, string>();
    }
}