using System.Collections.Generic;

namespace test
{
    public sealed class FileSummaryPayload
    {
        public IReadOnlyList<FileSummaryItem> Items { get; init; }
            = new List<FileSummaryItem>();

        public string Summary { get; init; } = "";
    }

    public sealed class FileSummaryItem
    {
        public string FileName { get; init; } = "";

        public string Kind { get; init; } = "";

        public string MimeType { get; init; } = "";

        public string RelativePath { get; init; } = "";

        public string FileType { get; init; } = "";

        public bool IsImage { get; init; }

        public bool IsPdf { get; init; }

        public bool IsTextLike { get; init; }

        public string ContentPreview { get; init; } = "";
    }
}