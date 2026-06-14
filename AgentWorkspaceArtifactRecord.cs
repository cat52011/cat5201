using System;
using System.Collections.Generic;

namespace test
{
    public sealed class AgentWorkspaceArtifactRecord
    {
        public string Id { get; init; } = "";

        public string RunId { get; init; } = "";

        public string NodeId { get; init; } = "";

        public string SourceAgentId { get; init; } = "";

        public string ItemType { get; init; } = "";

        public string ArtifactKind { get; init; } = "";

        public string ContentFormat { get; init; } = "";

        public bool IsUserVisible { get; init; }

        public string Title { get; init; } = "";

        public string Preview { get; init; } = "";

        public int EstimatedSize { get; init; }

        public int FactCount { get; init; }

        public DateTime CreatedAtUtc { get; init; }

        // Workspace v2：標準化 source metadata + status + dependencies + 友善標籤。
        public string ModelId { get; init; } = "";

        public string CapabilityId { get; init; } = "";

        public string Status { get; init; } = ArtifactStatus.Ready;

        public string StatusLabel { get; init; } = "";

        public IReadOnlyList<string> DependsOn { get; init; } = Array.Empty<string>();

        // 給產品介面用的中文化標籤（kind / format）。
        public string KindLabel { get; init; } = "";

        public string FormatLabel { get; init; } = "";

        // 若此 artifact 已落地成檔（報告 / 簡報 / 圖片），這裡是可開啟 / 匯出的路徑。
        public string FilePath { get; init; } = "";

        // 本機時間字串，避免 UI 端重複轉換。
        public string CreatedAtLocalText { get; init; } = "";
    }
}
