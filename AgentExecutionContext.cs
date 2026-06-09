using System;
using System.Collections.Generic;

namespace test
{
    public sealed class AgentExecutionContext
    {
        public NodeControl Node { get; init; } = null!;

        public AgentDefinition Agent { get; init; } = null!;

        public string AgentId => Agent?.Id ?? "";

        public string TopText { get; init; } = "";

        public NodeTaskMode TaskMode { get; init; } = NodeTaskMode.Chat;

        public IReadOnlyList<MainWindow.AttachmentInfo> Attachments { get; init; }
            = Array.Empty<MainWindow.AttachmentInfo>();

        public string AttachmentsRootDir { get; init; } = "";
    }
}
