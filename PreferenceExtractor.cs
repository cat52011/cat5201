using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    /// <summary>
    /// Memory v1 偏好擷取：用純規則（零額外 LLM 呼叫）從文字中辨識使用者偏好。
    /// 兩種來源：
    ///   1. 明確指令（手動輸入框，或任務文字含「記住/以後/都/盡量/請用/習慣/喜歡/不要」等意圖詞）。
    ///   2. 被動語言偵測（任務文字的主要書寫語言）。
    /// 每個偏好有穩定 Key（pref.language 等），同 Key 之後覆蓋前者（在 MemoryStore upsert）。
    /// </summary>
    public sealed class PreferenceExtractor
    {
        public sealed class DetectedPreference
        {
            public string Key { get; init; } = "";
            public string DisplayValue { get; init; } = "";
            public string Content { get; init; } = "";
            public double Importance { get; init; } = 0.9;
        }

        // 意圖詞：任務文字含這些字才從中被動擷取明確偏好（避免把每次任務內容誤判成偏好）。
        private static readonly string[] IntentWords =
        {
            "記住", "記得", "以後", "之後", "每次", "都用", "一律", "盡量", "儘量",
            "請用", "請改", "習慣", "喜歡", "偏好", "不要", "別再", "改成", "預設"
        };

        public bool HasIntentWord(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            return IntentWords.Any(w => text.Contains(w, StringComparison.Ordinal));
        }

        /// <summary>
        /// 從手動輸入框擷取：**整句原文完整保留**（不改寫、不截斷）。
        /// 類別偵測只用來決定 upsert 的 Key——若整句剛好對應單一已知類別（如只講語言），
        /// 用該類別 Key 讓之後同類別輸入可覆蓋；否則用整句雜湊當自訂 Key（不同句子並存）。
        /// </summary>
        public IReadOnlyList<DetectedPreference> ExtractFromManualInput(string text)
        {
            string raw = (text ?? "").Trim();
            if (raw.Length == 0)
                return new List<DetectedPreference>();

            var matched = MatchKnownCategories(raw);

            string key = matched.Count == 1
                ? matched[0].Key
                : "pref.custom:" + Hash(raw.ToLowerInvariant());

            return new List<DetectedPreference>
            {
                new DetectedPreference
                {
                    Key = key,
                    DisplayValue = raw,   // 清單顯示整句原文
                    Content = raw,        // 注入 prompt 的也是整句原文（LLM 直接看得懂）
                    Importance = 0.95
                }
            };
        }

        /// <summary>
        /// 從任務文字被動擷取：只有含意圖詞（記住/以後/盡量…）才取明確類別偏好。
        /// 注意：不做「輸入書寫語言 → 輸出語言」的被動推斷——使用者常用中文打字卻想要其他語言輸出，
        /// 那種推斷會誤把中文寫成偏好並蓋掉真正的語言需求。語言偏好一律只由明確陳述決定。
        /// </summary>
        public IReadOnlyList<DetectedPreference> ExtractFromTaskText(string text)
        {
            if (!HasIntentWord(text))
                return new List<DetectedPreference>();

            return MatchKnownCategories(text);
        }

        // 規則比對：語言 / 詳簡 / 模型成本 / 格式。
        private List<DetectedPreference> MatchKnownCategories(string text)
        {
            var list = new List<DetectedPreference>();
            if (string.IsNullOrWhiteSpace(text)) return list;
            string lower = text.ToLowerInvariant();

            // 語言 / 書寫系統：偵測「所有」提到的語言，支援雙語 / 多語共同輸出。
            var langs = new List<string>();
            if (Contains(text, "繁體中文", "繁中") || lower.Contains("traditional chinese"))
                langs.Add("繁體中文");
            else if (Contains(text, "簡體中文", "简体中文", "简体") || lower.Contains("simplified chinese"))
                langs.Add("簡體中文");
            else if (Contains(text, "中文", "國語", "華語") || lower.Contains("chinese"))
                langs.Add("繁體中文"); // 單講「中文」預設繁體

            if (Contains(text, "英文", "英語") || lower.Contains("english"))
                langs.Add("英文");
            if (Contains(text, "日文", "日語", "日本語") || lower.Contains("japanese"))
                langs.Add("日文");
            if (Contains(text, "韓文", "韓語", "한국어", "한국말") || lower.Contains("korean"))
                langs.Add("韓文");

            langs = langs.Distinct().ToList();
            if (langs.Count == 1)
            {
                list.Add(Pref("pref.language", langs[0], $"輸出語言：{langs[0]}", 0.95));
            }
            else if (langs.Count >= 2)
            {
                // 雙語 / 多語：同時用所有指定語言輸出（例如中文與韓文並列）。
                string joined = string.Join("、", langs);
                list.Add(Pref("pref.language", joined,
                    $"輸出語言：{joined} 同時並列輸出（每種語言都要呈現，不可只輸出其中一種）", 0.95));
            }

            // 詳簡
            if (Contains(text, "簡潔", "精簡", "簡短", "短一點", "扼要", "重點就好") || lower.Contains("concise") || lower.Contains("brief"))
                list.Add(Pref("pref.verbosity", "簡潔", "輸出風格：簡潔、重點導向，避免冗長", 0.9));
            else if (Contains(text, "詳細", "完整", "深入", "長一點", "鉅細靡遺") || lower.Contains("detailed") || lower.Contains("in depth"))
                list.Add(Pref("pref.verbosity", "詳細", "輸出風格：詳細、完整、深入說明", 0.9));

            // 模型成本
            if (Contains(text, "便宜", "省錢", "省一點", "低成本", "省額度", "別太貴") || lower.Contains("cheap") || lower.Contains("low cost"))
                list.Add(Pref("pref.model_cost", "省成本", "模型偏好：盡量選便宜 / 低成本模型，避免高價模型", 0.9));
            else if (Contains(text, "最好的", "最強", "高品質", "不計成本", "用好的") || lower.Contains("best model") || lower.Contains("high quality"))
                list.Add(Pref("pref.model_cost", "高品質", "模型偏好：優先品質，可使用較強 / 較貴模型", 0.9));

            // 格式
            if (Contains(text, "條列", "列點", "用點", "bullet") || lower.Contains("bullet"))
                list.Add(Pref("pref.format", "條列", "輸出格式：偏好條列 / 重點清單", 0.85));
            else if (Contains(text, "表格", "用表") || lower.Contains("table"))
                list.Add(Pref("pref.format", "表格", "輸出格式：適合時用表格整理", 0.85));
            else if (Contains(text, "段落", "純文字敘述"))
                list.Add(Pref("pref.format", "段落", "輸出格式：偏好段落式敘述", 0.85));

            return list;
        }

        private static DetectedPreference Pref(string key, string value, string content, double importance) =>
            new() { Key = key, DisplayValue = value, Content = content, Importance = importance };

        private static bool Contains(string text, params string[] needles) =>
            needles.Any(n => !string.IsNullOrEmpty(n) && text.Contains(n, StringComparison.Ordinal));

        private static string Truncate(string s, int max)
        {
            s = (s ?? "").Trim();
            return s.Length <= max ? s : s.Substring(0, max) + "…";
        }

        private static string Hash(string s)
        {
            // 穩定短雜湊，給自訂偏好做去重 key（相同文字 → 相同 key）。
            unchecked
            {
                int h = 23;
                foreach (char c in s) h = h * 31 + c;
                return (h & 0x7FFFFFFF).ToString();
            }
        }
    }
}
