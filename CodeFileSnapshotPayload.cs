using System.Collections.Generic;

namespace test
{
    public sealed class CodeFileSnapshotPayload
    {
        public IReadOnlyList<CodeFileSnapshotItem> Files { get; init; }
            = new List<CodeFileSnapshotItem>();

        public string Summary { get; init; } = "";
    }

    public sealed class CodeFileSnapshotItem
    {
        public string FileName { get; init; } = "";

        public string RelativePath { get; init; } = "";

        public string FileType { get; init; } = "";

        public string Language { get; init; } = "";

        public int CharacterCount { get; init; }

        public int LineCount { get; init; }

        public bool IsTruncated { get; init; }

        public string Content { get; init; } = "";
    }
}
