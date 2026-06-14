using System.Collections.Generic;
using System.Linq;

namespace test
{
    /// <summary>
    /// Code Agent v1.5：依附件 snapshot 評估程式任務的規模與風險，產生使用者可見的提醒。
    /// 純規則、零 LLM 呼叫，不耗額度。
    /// </summary>
    public static class CodeTaskAssessor
    {
        public static CodeTaskAssessmentPayload Assess(CodeFileSnapshotPayload? snapshot)
        {
            if (snapshot?.Files == null || snapshot.Files.Count == 0)
            {
                return new CodeTaskAssessmentPayload
                {
                    Summary = "No code snapshot available for assessment."
                };
            }

            var files = snapshot.Files.Where(f => f != null).ToList();
            int fileCount = files.Count;
            int totalLines = files.Sum(f => f.LineCount);
            int totalChars = files.Sum(f => f.CharacterCount);
            bool anyTruncated = files.Any(f => f.IsTruncated);

            string sizeTier =
                (totalChars >= 40000 || totalLines >= 1200 || fileCount >= 6) ? "large" :
                (totalChars >= 12000 || totalLines >= 400 || fileCount >= 3) ? "medium" :
                "small";

            string risk =
                sizeTier == "large" ? "high" :
                sizeTier == "medium" ? "medium" :
                "low";

            if (anyTruncated && risk == "low")
                risk = "medium";

            // 超過單檔 prompt 預算或有截斷，就代表本次只能送出重點區段（分段分析）。
            bool chunked = anyTruncated || totalChars > 18000;

            var warnings = new List<string>();

            if (sizeTier == "large")
            {
                warnings.Add($"大型程式任務：{fileCount} 個檔案、約 {totalLines} 行（{totalChars} 字元）。");
                warnings.Add("本次只會送出重點區段給模型以控制成本，分析可能不涵蓋整份程式碼。");
                warnings.Add("建議：縮小到單一檔案 / 單一功能，或先用「列出 bug」盤點後再逐項修正。");
            }
            else if (sizeTier == "medium")
            {
                warnings.Add($"中型程式任務：{fileCount} 個檔案、約 {totalLines} 行。");
                if (anyTruncated)
                    warnings.Add("部分檔案內容過長已截斷，分析以可見內容為準。");
            }

            string summary =
                $"Size={sizeTier}, Risk={risk}, Files={fileCount}, " +
                $"Lines={totalLines}, Chars={totalChars}, Chunked={chunked}";

            return new CodeTaskAssessmentPayload
            {
                SizeTier = sizeTier,
                RiskLevel = risk,
                FileCount = fileCount,
                TotalLines = totalLines,
                TotalChars = totalChars,
                ContextChunked = chunked,
                Summary = summary,
                Warnings = warnings
            };
        }
    }
}
