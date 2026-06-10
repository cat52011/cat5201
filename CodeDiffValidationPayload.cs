using System.Collections.Generic;

namespace test
{
    public sealed class CodeDiffValidationPayload
    {
        public string Status { get; init; } = "unchecked";
        // valid / warning / invalid / unchecked

        public string Summary { get; init; } = "";

        public IReadOnlyList<CodeDiffValidationFileResult> Files { get; init; }
            = new List<CodeDiffValidationFileResult>();

        public IReadOnlyList<string> Messages { get; init; }
            = new List<string>();
    }

    public sealed class CodeDiffValidationFileResult
    {
        public string Path { get; init; } = "";

        public string Status { get; init; } = "unchecked";

        public int AddedLines { get; init; }

        public int RemovedLines { get; init; }

        public string Message { get; init; } = "";
    }
}
