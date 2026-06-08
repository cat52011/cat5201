using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class SearchCapability : IAgentCapability
    {
        private readonly PerplexityService _service;

        public SearchCapability(PerplexityService service)
        {
            _service = service;
        }

        public string Id => "search-capability";

        public AgentCapability RequiredAgentCapability => AgentCapability.Search;

        public bool CanHandle(AgentExecutionContext context)
        {
            if (context == null)
                return false;

            if (context.TaskMode == NodeTaskMode.Research)
                return true;

            string text = context.TopText ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("查詢") ||
                   text.Contains("搜尋") ||
                   text.Contains("查證") ||
                   text.Contains("最新") ||
                   text.Contains("比較") ||
                   text.Contains("財報") ||
                   text.Contains("股價") ||
                   text.Contains("走勢") ||
                   text.Contains("research", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("latest", StringComparison.OrdinalIgnoreCase);
        }

        public async Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct)
        {
            string originalQuery = context.TopText ?? "";

            bool financeQuery =
                IsStockFinanceQuery(originalQuery) ||
                DetectTickers(originalQuery).Count > 0;

            if (financeQuery)
            {
                return await ExecuteAuthoritativeFinanceResearchAsync(
                    originalQuery,
                    ct);
            }

            return await ExecuteGeneralSearchAsync(
                originalQuery,
                ct);
        }

        private async Task<AgentCapabilityResult> ExecuteAuthoritativeFinanceResearchAsync(
            string originalQuery,
            CancellationToken ct)
        {
            string tickers = string.Join(", ", DetectTickers(originalQuery));

            string instructions =
$@"你是金融研究代理，負責提供可供下游 agent 使用的最新金融事實。

你必須使用 Perplexity / web search 的最新資料。

目前使用者問題：
{originalQuery}

若有股票代號，目標股票代號為：
{tickers}

核心規則：
1. 對於「最新股價」，請優先使用 Perplexity 搜尋結果中最像即時報價卡、金融資料卡、交易所/券商即時報價、Yahoo Finance、Nasdaq、MarketWatch、CNBC、Google Finance 類型的最新資料。
2. 每個 ticker 只輸出一個 primary latest price。
3. 不要把不同網站的舊收盤價、盤前價、盤後價、歷史價格、目標價、預測價全部混在一起。
4. 只有在你確定同一個 ticker 的最新價格來源真的互相衝突，而且無法判斷哪一個較新或較可靠時，才列「資料衝突」。
5. 若某個價格明顯像歷史價、目標價、錯誤映射、不同日期舊資料，請不要列入 primary facts，只能列入 ignored notes。
6. 財報資料請使用最新一季或最新公司指引；不要混用不同年度或不同季度。
7. 不要自己發明數字。
8. 不要使用 GPT 內部知識補資料。
9. 若找不到某欄位，該欄位寫「未取得」，不要整體回答資料不足。
10. 請輸出乾淨、短而結構化的繁體中文結果，供後續 final synthesizer 使用。

請嚴格使用以下格式：

【Primary Facts】
Ticker: 
Company:
Latest Price:
Price Time:
Market Session:
Revenue:
EPS:
Gross Margin:
Guidance:
Key Market Drivers:

【Conflicts】
只列真正不可判斷的重大衝突。若沒有，寫：無重大衝突。

【Ignored / Low Confidence】
列出你排除不用的舊資料、疑似錯誤資料、歷史價格、目標價或來源不明數字。若沒有，寫：無。

【Research Summary】
用 5～8 點整理市場、財報、短期走勢相關重點。";

            string answer = await _service.GenerateAgentAsync(
                instructions: instructions,
                userText: originalQuery,
                enableWebSearch: true,
                maxOutputTokens: 3000,
                ct: ct);

            if (string.IsNullOrWhiteSpace(answer))
            {
                return AgentCapabilityResult.DirectHandle(
                    "search-capability required authoritative finance research, but Perplexity returned empty result.");
            }

            return BuildAuthoritativeFinanceResult(
                originalQuery,
                answer); return BuildAuthoritativeFinanceResult(
                originalQuery,
                answer);
        }

        private async Task<AgentCapabilityResult> ExecuteGeneralSearchAsync(
            string originalQuery,
            CancellationToken ct)
        {
            string searchQuery = BuildSearchQuery(originalQuery);

            var results = await _service.SearchAsync(
                searchQuery,
                maxResults: 5,
                ct: ct);

            if (results == null || results.Count == 0)
                return AgentCapabilityResult.NotHandled();

            var cleaned = results
                .Where(x => x != null && !string.IsNullOrWhiteSpace(x.Title))
                .GroupBy(x => NormalizeTitle(x.Title), StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            if (cleaned.Count == 0)
                return AgentCapabilityResult.NotHandled();

            var items = cleaned
                .Select(x => new SearchSummaryItem
                {
                    Title = x.Title ?? "",
                    KeyPoint = ExtractKeyPoint(x.Snippet),
                    Source = x.Url ?? "",
                    Date = x.Date ?? ""
                })
                .ToList();

            string summary = BuildSummary(originalQuery, items);

            var payload = new SearchSummaryPayload
            {
                Query = originalQuery,
                Summary = summary,
                Items = items
            };

            var result = AgentCapabilityResult.WithData("search_summary", payload);

            var verifiedFacts = BuildVerifiedFacts(originalQuery, items);
            if (verifiedFacts.Facts.Count > 0)
                result.Data["verified_facts"] = verifiedFacts;

            return result;
        }

        private AgentCapabilityResult BuildAuthoritativeFinanceResult(
            string query,
            string answer)
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

            string cleanedAnswer = CleanFinanceResearchAnswer(answer);

            var searchPayload = new SearchSummaryPayload
            {
                Query = query ?? "",
                Summary = cleanedAnswer,
                Items = new List<SearchSummaryItem>
                {
                    new SearchSummaryItem
                    {
                        Title = "Perplexity authoritative finance research",
                        KeyPoint = cleanedAnswer,
                        Source = "Perplexity Sonar",
                        Date = now
                    }
                }
            };

            var verifiedPayload = new VerifiedFactPayload
            {
                Query = query ?? "",
                Summary = cleanedAnswer,
                Facts = new List<VerifiedFactItem>
                {
                    new VerifiedFactItem
                    {
                        Subject = BuildVerifiedSubject(query),
                        FactType = "authoritative_finance_research",
                        Value = cleanedAnswer,
                        Unit = "",
                        AsOf = now,
                        SourceTitle = "Perplexity Sonar",
                        SourceUrl = "",
                        Confidence = "high"
                    }
                }
            };

            var result = AgentCapabilityResult.WithData("verified_facts", verifiedPayload);
            result.Data["search_summary"] = searchPayload;

            return result;
        }

        private static VerifiedFactPayload BuildVerifiedFacts(
            string query,
            IReadOnlyList<SearchSummaryItem> items)
        {
            var facts = new List<VerifiedFactItem>();

            if (items == null || items.Count == 0)
            {
                return new VerifiedFactPayload
                {
                    Query = query ?? "",
                    Facts = facts,
                    Summary = ""
                };
            }

            var topItems = items
                .Take(5)
                .ToList();

            foreach (var item in topItems)
            {
                if (item == null)
                    continue;

                string subject = DetectPrimarySubject(
                    query,
                    item.Title + " " + item.KeyPoint);

                if (string.IsNullOrWhiteSpace(subject))
                    subject = "search_result";

                facts.Add(new VerifiedFactItem
                {
                    Subject = subject,
                    FactType = "general_search_context",
                    Value = item.KeyPoint ?? "",
                    Unit = "",
                    AsOf = item.Date ?? "",
                    SourceTitle = item.Title ?? "",
                    SourceUrl = item.Source ?? "",
                    Confidence = "medium"
                });
            }

            return new VerifiedFactPayload
            {
                Query = query ?? "",
                Facts = facts,
                Summary = BuildVerifiedFactSummary(facts)
            };
        }

        private static string DetectPrimarySubject(
            string query,
            string content)
        {
            foreach (var ticker in DetectTickers(query))
            {
                if (content.Contains(ticker, StringComparison.OrdinalIgnoreCase))
                    return ticker;

                if (content.Contains(TickerAlias(ticker), StringComparison.OrdinalIgnoreCase))
                    return ticker;
            }

            return "";
        }

        private static string BuildVerifiedFactSummary(IReadOnlyList<VerifiedFactItem> facts)
        {
            if (facts == null || facts.Count == 0)
                return "";

            var sb = new StringBuilder();
            sb.AppendLine("已驗證事實如下：");

            foreach (var fact in facts)
            {
                if (fact == null)
                    continue;

                string unit = string.IsNullOrWhiteSpace(fact.Unit)
                    ? ""
                    : $" {fact.Unit}";

                string asOf = string.IsNullOrWhiteSpace(fact.AsOf)
                    ? ""
                    : $"（時間/日期：{fact.AsOf}）";

                sb.AppendLine($"- {fact.Subject}: {fact.Value}{unit}{asOf}");
            }

            return sb.ToString().Trim();
        }

        private static string BuildVerifiedSubject(string query)
        {
            var tickers = DetectTickers(query);

            if (tickers.Count > 0)
                return string.Join(", ", tickers);

            return "Perplexity Research";
        }

        private static string TickerAlias(string ticker)
        {
            return ticker?.ToUpperInvariant() switch
            {
                "TSM" => "台積電",
                "MU" => "美光",
                "NVDA" => "輝達",
                "AMD" => "超微",
                "TSLA" => "特斯拉",
                "AAPL" => "蘋果",
                "MSFT" => "微軟",
                _ => ticker ?? ""
            };
        }

        private static string BuildSearchQuery(string query)
        {
            if (string.IsNullOrWhiteSpace(query))
                return "";

            return query;
        }

        private static bool IsStockFinanceQuery(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return ContainsAny(
                text,
                "股價", "美股", "財報", "營收", "EPS", "毛利率", "走勢", "短期", "預測",
                "stock price", "quote", "earnings", "revenue", "guidance", "outlook",
                "TSM", "MU", "NVDA", "AMD", "TSLA", "AAPL", "MSFT");
        }

        private static List<string> DetectTickers(string text)
        {
            var result = new List<string>();

            AddTickerIfMentioned(result, text, "TSM", "TSM", "台積電", "台積");
            AddTickerIfMentioned(result, text, "MU", "MU", "美光", "Micron");
            AddTickerIfMentioned(result, text, "NVDA", "NVDA", "輝達", "NVIDIA");
            AddTickerIfMentioned(result, text, "AMD", "AMD", "超微");
            AddTickerIfMentioned(result, text, "TSLA", "TSLA", "Tesla", "特斯拉");
            AddTickerIfMentioned(result, text, "AAPL", "AAPL", "Apple", "蘋果");
            AddTickerIfMentioned(result, text, "MSFT", "MSFT", "Microsoft", "微軟");

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static void AddTickerIfMentioned(
            List<string> result,
            string text,
            string ticker,
            params string[] aliases)
        {
            if (result == null || string.IsNullOrWhiteSpace(text))
                return;

            foreach (var alias in aliases)
            {
                if (!string.IsNullOrWhiteSpace(alias) &&
                    text.Contains(alias, StringComparison.OrdinalIgnoreCase))
                {
                    result.Add(ticker);
                    return;
                }
            }
        }

        private static string ExtractKeyPoint(string snippet)
        {
            if (string.IsNullOrWhiteSpace(snippet))
                return "";

            snippet = snippet.Trim();

            return snippet.Length > 220
                ? snippet.Substring(0, 220)
                : snippet;
        }

        private static string BuildSummary(
            string query,
            IEnumerable<SearchSummaryItem> items)
        {
            var list = items?
                .Where(x => x != null)
                .ToList() ?? new List<SearchSummaryItem>();

            if (list.Count == 0)
                return "無明確重點資訊";

            var sb = new StringBuilder();
            sb.AppendLine("整理重點如下：");

            int index = 1;
            foreach (var item in list)
            {
                if (string.IsNullOrWhiteSpace(item.KeyPoint))
                    continue;

                sb.AppendLine($"{index}. {item.KeyPoint}");

                if (!string.IsNullOrWhiteSpace(item.Date))
                    sb.AppendLine($"   Date: {item.Date}");

                if (!string.IsNullOrWhiteSpace(item.Source))
                    sb.AppendLine($"   Source: {item.Source}");

                index++;
            }

            if (index == 1)
                return "無明確重點資訊";

            return sb.ToString().Trim();
        }

        private static string NormalizeTitle(string title)
        {
            if (string.IsNullOrWhiteSpace(title))
                return "";

            return title.Trim().ToLowerInvariant();
        }

        private static bool ContainsAny(string text, params string[] keywords)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            foreach (var keyword in keywords)
            {
                if (!string.IsNullOrWhiteSpace(keyword) &&
                    text.Contains(keyword, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static string CleanFinanceResearchAnswer(string answer)
        {
            if (string.IsNullOrWhiteSpace(answer))
                return "";

            var lines = answer
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(x => x.TrimEnd())
                .ToList();

            var cleaned = new List<string>();

            foreach (var line in lines)
            {
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (cleaned.Count > 0 && !string.IsNullOrWhiteSpace(cleaned[^1]))
                        cleaned.Add("");

                    continue;
                }

                if (line.Contains("[") && line.Contains("]"))
                {
                    cleaned.Add(RemoveSimpleCitationMarkers(line));
                }
                else
                {
                    cleaned.Add(line);
                }
            }

            return string.Join(Environment.NewLine, cleaned)
                .Trim();
        }

        private static string RemoveSimpleCitationMarkers(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var sb = new StringBuilder();

            for (int i = 0; i < text.Length; i++)
            {
                if (text[i] == '[')
                {
                    int end = text.IndexOf(']', i + 1);

                    if (end > i)
                    {
                        string inside = text.Substring(i + 1, end - i - 1);

                        if (inside.All(c => char.IsDigit(c) || c == ',' || c == ' ' || c == '-'))
                        {
                            i = end;
                            continue;
                        }
                    }
                }

                sb.Append(text[i]);
            }

            return sb.ToString().Trim();
        }
    }
}