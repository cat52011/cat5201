using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace test
{
    /// <summary>
    /// §7 NotebookLM 匯出輔助（B 方案）：Google 尚未開放 NotebookLM API 前的過渡路徑。
    /// 把一次研究／簡報任務的素材打包成「NotebookLM 可匯入的來源文字包」，使用者在 NotebookLM
    /// 以「貼上文字／上傳檔案」加入即可，作為簡報生成的另一條路。
    ///
    /// 純字串組裝、不呼叫任何 API；之後接上官方 API 時只要改呼叫端（A 方案），本檔可重用。
    /// </summary>
    public static class NotebookLmBundleBuilder
    {
        public static string Build(string? title, string? topic, string? synthesis, AgentWorkspace? workspace)
        {
            string deckTitle = string.IsNullOrWhiteSpace(title) ? "研究素材" : title.Trim();
            var sources = CollectSources(workspace);

            var sb = new StringBuilder();
            sb.AppendLine($"# {deckTitle} — NotebookLM 匯入包");
            sb.AppendLine();
            sb.AppendLine("> 用法：開啟 NotebookLM → 新增來源 → 選「複製的文字／貼上文字」，把本檔內容整段貼入；");
            sb.AppendLine("> 或直接上傳本檔。下方「來源網址」可逐一以「網站」來源加入，讓 NotebookLM 直接讀原始出處。");
            sb.AppendLine();

            if (!string.IsNullOrWhiteSpace(topic))
            {
                sb.AppendLine("## 主旨");
                sb.AppendLine(topic.Trim());
                sb.AppendLine();
            }

            sb.AppendLine("## 內容");
            sb.AppendLine(string.IsNullOrWhiteSpace(synthesis) ? "（無內容）" : synthesis.Trim());
            sb.AppendLine();

            if (sources.Count > 0)
            {
                sb.AppendLine("## 參考來源");
                int i = 1;
                foreach (var (label, url) in sources)
                {
                    sb.AppendLine(string.IsNullOrWhiteSpace(url)
                        ? $"{i}. {label}"
                        : $"{i}. {label} — {url}");
                    i++;
                }
                sb.AppendLine();

                var urls = sources
                    .Where(s => !string.IsNullOrWhiteSpace(s.Url))
                    .Select(s => s.Url)
                    .Distinct()
                    .ToList();

                if (urls.Count > 0)
                {
                    sb.AppendLine("## 來源網址（可逐一加為 NotebookLM 網站來源）");
                    foreach (var u in urls)
                        sb.AppendLine(u);
                    sb.AppendLine();
                }
            }

            sb.AppendLine("---");
            sb.AppendLine($"產生時間：{DateTime.Now:yyyy-MM-dd HH:mm}");
            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        // 從 workspace 的 verified_facts 收斂出不重複的來源（標題 + 網址），最多 20 筆。
        private static List<(string Label, string Url)> CollectSources(AgentWorkspace? workspace)
        {
            var result = new List<(string, string)>();
            if (workspace == null)
                return result;

            var facts = workspace.GetByType("verified_facts")
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .SelectMany(p => p.Facts ?? Array.Empty<VerifiedFactItem>())
                .Where(f => f != null &&
                    (!string.IsNullOrWhiteSpace(f.SourceTitle) || !string.IsNullOrWhiteSpace(f.SourceUrl)))
                .GroupBy(f => (f.SourceTitle ?? "").Trim() + "|" + (f.SourceUrl ?? "").Trim())
                .Select(g => g.First())
                .Take(20);

            foreach (var f in facts)
            {
                string label = string.IsNullOrWhiteSpace(f.SourceTitle)
                    ? (f.SourceUrl ?? "").Trim()
                    : f.SourceTitle.Trim();
                result.Add((label, (f.SourceUrl ?? "").Trim()));
            }

            return result;
        }
    }
}
