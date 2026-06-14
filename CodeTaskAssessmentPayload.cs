using System.Collections.Generic;

namespace test
{
    /// <summary>
    /// Code Agent v1.5：程式任務的規模 / 風險評估。屬「過程資訊」，顯示在決策區，不混進輸出端。
    /// </summary>
    public sealed class CodeTaskAssessmentPayload
    {
        // small / medium / large
        public string SizeTier { get; init; } = "small";

        // low / medium / high
        public string RiskLevel { get; init; } = "low";

        public int FileCount { get; init; }
        public int TotalLines { get; init; }
        public int TotalChars { get; init; }

        // 是否因為過大而只送出重點區段給模型（分段分析）。
        public bool ContextChunked { get; init; }

        public string Summary { get; init; } = "";

        public IReadOnlyList<string> Warnings { get; init; } = new List<string>();
    }
}
