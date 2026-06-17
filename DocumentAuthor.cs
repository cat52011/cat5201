using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace test
{
    /// <summary>
    /// §6/§7 內容作者：把（可能含 ASCII 排版、簡報草稿、雜訊的）主答案，
    /// 重新整理成「乾淨、適合該檔案格式」的內容。
    /// - 報告（docx/pdf）：請模型寫成正式書面報告 Markdown（標題 / 段落 / 標準表格），禁止 ASCII 線框。
    /// - 表格（xlsx/pdf）：請模型只輸出一個乾淨的 Markdown 表格。
    /// 模型失敗時用 Sanitize / ExtractTable 當安全網，至少不把 ASCII 垃圾寫進檔案。
    /// </summary>
    public static class DocumentAuthor
    {
        // ---- 提示詞 ----

        public static string BuildReportPrompt(string userInput, string sourceMaterial)
        {
            return
                "你是專業的報告撰寫者。請依下列資料，寫出一份結構清楚、可直接交付的【書面報告】。\n\n" +
                "主題 / 使用者要求：\n" + (userInput ?? "").Trim() + "\n\n" +
                "可參考的內容與數據（可能含雜訊、ASCII 排版或簡報草稿，請只擷取其中的事實與數據後重新組織）：\n" +
                Clip(sourceMaterial, 8000) + "\n\n" +
                "嚴格輸出規則：\n" +
                "1. 只輸出 Markdown 報告本身，不要任何前後說明，不要程式碼圍欄(```）。\n" +
                "2. 不要加最上層的 # 一級標題（標題由系統補）；章節用 ## ，小節用 ### 。\n" +
                "3. 內文用完整段落（繁體中文）。要並列數據時用標準 Markdown 表格：| 欄 | 欄 | 之後接 |---|---| 分隔列。\n" +
                "4. 嚴禁使用 ASCII 線框 / 方塊字元（┌ ┐ └ ┘ │ ─ █ ═ ║ 等）來畫表格或排版。\n" +
                "5. 嚴禁出現「投影片」「slide」這類簡報用語——這是書面報告，不是簡報。\n" +
                "6. 不要用 emoji 當裝飾性項目符號。";
        }

        public static string BuildTablePrompt(string userInput, string sourceMaterial)
        {
            return
                "你是資料整理者。請把下列內容整理成一個乾淨的【Markdown 表格】。\n\n" +
                "主題 / 使用者要求：\n" + (userInput ?? "").Trim() + "\n\n" +
                "可參考的內容與數據（可能含雜訊或 ASCII 排版，請擷取其中可表格化的資料）：\n" +
                Clip(sourceMaterial, 8000) + "\n\n" +
                "嚴格輸出規則：\n" +
                "1. 只輸出一個 Markdown 表格，不要任何標題、說明、前後文字，不要程式碼圍欄(```）。\n" +
                "2. 第一列為欄位標題，第二列為 |---|---| 分隔列，其後每列一筆資料。\n" +
                "3. 每列欄位數一致；缺值填「—」。\n" +
                "4. 用繁體中文；數字保留原始單位。\n" +
                "5. 嚴禁 ASCII 線框字元（┌ ┐ │ ─ 等），一律用標準 Markdown 管線符號 | 分隔。";
        }

        // ---- 清理模型輸出 ----

        /// <summary>去掉模型偶爾包的程式碼圍欄。</summary>
        public static string StripCodeFence(string? text)
        {
            string t = (text ?? "").Trim();
            if (t.StartsWith("```", StringComparison.Ordinal))
            {
                int firstNl = t.IndexOf('\n');
                if (firstNl >= 0) t = t.Substring(firstNl + 1);
                if (t.EndsWith("```", StringComparison.Ordinal))
                    t = t.Substring(0, t.Length - 3);
            }
            return t.Trim();
        }

        /// <summary>
        /// 內容是否「髒」（含 ASCII 線框 / 方塊字元，或簡報草稿措辭）——只有髒的時候才需要請作者重寫，
        /// 已經是乾淨散文的報告就直接用，省一次模型呼叫也避免改壞。
        /// </summary>
        public static bool LooksDirty(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return false;
            foreach (char c in text)
                if ((c >= '─' && c <= '▟') || c == '█')
                    return true;
            return text.Contains("投影片", StringComparison.Ordinal);
        }

        // ---- 安全網：清掉 ASCII 線框 / 簡報草稿裝飾 ----

        /// <summary>
        /// 移除 ASCII 線框 / 方塊字元、簡報草稿標記（「投影片 N｜」）與裝飾性符號，
        /// 讓退回主答案時也不至於把 ASCII 垃圾寫進報告。
        /// </summary>
        public static string Sanitize(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var outLines = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                string line = raw;

                // 「🖼️ 投影片 3｜FQ2 財報」→「### FQ2 財報」：保留標題、丟掉簡報措辭。
                var slideMatch = Regex.Match(line, @"投影片\s*\d+\s*[｜|]\s*(.+)$");
                if (slideMatch.Success)
                {
                    string heading = StripBoxChars(slideMatch.Groups[1].Value).Trim();
                    if (heading.Length > 0)
                        outLines.Add("### " + heading);
                    continue;
                }

                string cleaned = StripBoxChars(line);

                // 整行只剩線框 / 空白 / 標點 → 丟棄（原本是純排版線）。
                string probe = Regex.Replace(cleaned, @"[\s\-_=·•\.]", "");
                if (probe.Length == 0)
                {
                    // 保留真正的空行以維持段距，但不要連續多個。
                    if (outLines.Count > 0 && outLines[^1].Length != 0)
                        outLines.Add("");
                    continue;
                }

                outLines.Add(cleaned.TrimEnd());
            }

            // 收尾：壓掉開頭 / 結尾空行。
            return string.Join("\n", outLines).Trim();
        }

        /// <summary>從一段文字裡抓出第一個 Markdown 表格區塊；找不到回 null。</summary>
        public static string? ExtractMarkdownTable(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return null;

            var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            for (int i = 0; i + 1 < lines.Length; i++)
            {
                if (IsPipeRow(lines[i]) && IsSeparatorRow(lines[i + 1]))
                {
                    var block = new List<string> { lines[i].Trim(), lines[i + 1].Trim() };
                    int j = i + 2;
                    while (j < lines.Length && IsPipeRow(lines[j]))
                    {
                        block.Add(lines[j].Trim());
                        j++;
                    }
                    return string.Join("\n", block);
                }
            }
            return null;
        }

        // ---- 私有輔助 ----

        private static string StripBoxChars(string line)
        {
            // U+2500–257F 線框、U+2580–259F 方塊、全形重複塊；保留其餘文字。
            var sb = new StringBuilder(line.Length);
            foreach (char c in line)
            {
                if ((c >= '─' && c <= '▟') || c == '█')
                    continue;
                sb.Append(c);
            }
            return sb.ToString();
        }

        private static bool IsPipeRow(string? line)
        {
            string t = (line ?? "").Trim();
            return t.Length >= 3 && t.StartsWith("|", StringComparison.Ordinal) && t.IndexOf('|', 1) >= 1;
        }

        private static bool IsSeparatorRow(string? line)
        {
            string t = (line ?? "").Trim();
            if (!t.StartsWith("|", StringComparison.Ordinal))
                return false;
            bool sawDash = false;
            foreach (char c in t)
            {
                if (c == '-') sawDash = true;
                else if (c != '|' && c != ':' && c != ' ' && c != '\t')
                    return false;
            }
            return sawDash;
        }

        private static string Clip(string? text, int max)
        {
            string t = (text ?? "").Trim();
            return t.Length <= max ? t : t.Substring(0, max) + "…";
        }
    }
}
