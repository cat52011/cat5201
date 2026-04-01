namespace test
{
    public sealed class AiAttachment
    {
        public string FileName { get; init; } = "";
        public string AbsolutePath { get; init; } = "";
        public string RelativePath { get; init; } = "";
        public string MimeType { get; init; } = "application/octet-stream";
        public string Kind { get; init; } = "file";

        public bool IsImage =>
            string.Equals(Kind, "image", System.StringComparison.OrdinalIgnoreCase);

        public bool IsFile =>
            !IsImage;
    }
}