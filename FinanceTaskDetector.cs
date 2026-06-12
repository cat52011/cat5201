using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace test
{
    public static class FinanceTaskDetector
    {
        private static readonly Regex CashtagRegex =
            new(@"\$[A-Z]{1,6}(?:\.[A-Z])?\b", RegexOptions.Compiled);

        private static readonly IReadOnlyDictionary<string, string[]> KnownTickerAliases =
            new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
            {
                ["TSM"] = new[] { "TSM", "台積電", "台積", "TSMC" },
                ["MU"] = new[] { "MU", "美光", "Micron" },
                ["NVDA"] = new[] { "NVDA", "輝達", "NVIDIA" },
                ["AMD"] = new[] { "AMD", "超微" },
                ["TSLA"] = new[] { "TSLA", "Tesla", "特斯拉" },
                ["AAPL"] = new[] { "AAPL", "Apple", "蘋果" },
                ["MSFT"] = new[] { "MSFT", "Microsoft", "微軟" }
            };

        private static readonly string[] FinanceIntentTerms =
        {
            "股價", "股票", "美股", "財報", "營收", "毛利率", "EPS", "指引",
            "收盤", "盤後", "盤前", "即時價", "價格", "報價", "市值", "估值",
            "短期", "走勢", "漲跌", "目標價", "投資", "券商",
            "stock", "quote", "price", "earnings", "revenue", "guidance",
            "close", "after-hours", "pre-market", "premarket", "market cap",
            "valuation", "target price"
        };

        private static readonly string[] FreshnessTerms =
        {
            "最新", "即時", "今天", "現在", "目前", "查詢", "搜尋", "查證",
            "latest", "current", "today", "now", "search", "research"
        };

        /// <summary>
        /// 廣義：這個任務「牽涉公司 / 需要外部研究」。公司名（如 台積電）即算數。
        /// 用於決定要不要跑 search-capability、要不要 fresh facts。
        /// </summary>
        public static bool IsFinanceLike(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return HasFinanceIntent(text) ||
                   DetectKnownTickers(text).Count > 0 ||
                   DetectCashtags(text).Count > 0;
        }

        /// <summary>
        /// 狹義：這個任務「真的在問財經 / 報價」。需要真正的財經意圖（財報、股價、營收…）
        /// 或明確的 cashtag（$TSM）；單純提到公司名（例如「介紹台積電的簡報」）不算。
        /// 用於決定要不要啟用「報價卡財經研究模式」與「財經格式最終合成」。
        /// </summary>
        public static bool IsFinanceFocused(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return HasFinanceIntent(text) ||
                   DetectCashtags(text).Count > 0;
        }

        public static bool RequiresFreshFacts(string? text, NodeTaskMode taskMode)
        {
            if (taskMode == NodeTaskMode.Research)
                return true;

            if (string.IsNullOrWhiteSpace(text))
                return false;

            if (ContainsAny(text, FreshnessTerms))
                return true;

            if (!IsFinanceLike(text))
                return false;

            return HasFinanceIntent(text) ||
                   ContainsAny(text, FreshnessTerms) ||
                   DetectKnownTickers(text).Count > 0 ||
                   DetectCashtags(text).Count > 0;
        }

        public static IReadOnlyList<string> DetectKnownTickers(string? text)
        {
            var result = new List<string>();

            if (string.IsNullOrWhiteSpace(text))
                return result;

            foreach (var pair in KnownTickerAliases)
            {
                if (pair.Value.Any(alias => ContainsAlias(text, alias)))
                    result.Add(pair.Key);
            }

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        public static IReadOnlyList<string> DetectCashtags(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            return CashtagRegex
                .Matches(text)
                .Select(m => m.Value.TrimStart('$').ToUpperInvariant())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static bool HasFinanceIntent(string text)
        {
            return ContainsAny(text, FinanceIntentTerms);
        }

        private static bool ContainsAlias(string text, string alias)
        {
            if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(alias))
                return false;

            if (alias.All(c => char.IsLetterOrDigit(c)) && alias.All(c => c < 128))
                return Regex.IsMatch(text, $@"(?<![A-Za-z0-9]){Regex.Escape(alias)}(?![A-Za-z0-9])", RegexOptions.IgnoreCase);

            return text.Contains(alias, StringComparison.OrdinalIgnoreCase);
        }

        private static bool ContainsAny(string text, IEnumerable<string> terms)
        {
            foreach (var term in terms)
            {
                if (!string.IsNullOrWhiteSpace(term) &&
                    text.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}
