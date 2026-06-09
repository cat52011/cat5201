using System.Collections.Generic;

namespace test
{
    public sealed class CodeDiffArtifactPayload
    {
        public string Title { get; init; } = "";

        public string Status { get; init; } = "draft";
        // draft / ready / applied / rejected

        public string BaseLabel { get; init; } = "";

        public string TargetLabel { get; init; } = "";

        public IReadOnlyList<CodeDiffFileChange> Files { get; init; }
            = new List<CodeDiffFileChange>();

        public string UnifiedDiff { get; init; } = "";

        public IReadOnlyList<string> Notes { get; init; }
            = new List<string>();
    }

    public sealed class CodeDiffFileChange
    {
        public string Path { get; init; } = "";

        public string ChangeType { get; init; } = "modify";
        // add / modify / delete / rename

        public int AddedLines { get; init; }

        public int RemovedLines { get; init; }

        public string Summary { get; init; } = "";
    }
}
