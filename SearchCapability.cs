using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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

            bool forceFinanceResearch =
                IsStockFinanceQuery(originalQuery) ||
                DetectTickers(originalQuery).Count > 0;

            if (forceFinanceResearch)
            {
                string answer = await _service.GenerateAgentAsync(
                    instructions:
            @"你是金融研究代理。

請使用最新網路資料回答。

規則：

1. 若使用者問股價：
必須回答：
Ticker / 最新價格 / 交易時間

2. 若問財報：
必須回答：
營收 / EPS / 毛利率 / 指引

3. 若來源衝突：
分開列出，不要自己合併

4. 禁止回答『資料不足』，除非真的完全查不到。",
                    userText: originalQuery,
                    enableWebSearch: true,
                    maxOutputTokens: 4000,
                    ct: ct);

                if (!string.IsNullOrWhiteSpace(answer))
                {
                    return BuildAuthoritativeFinanceResult(
                        originalQuery,
                        answer);
                }
            }
            string searchQuery = BuildSearchQuery(originalQuery);

            var results = await _service.SearchAsync(
                searchQuery,
                maxResults: IsStockFinanceQuery(originalQuery) ? 10 : 5,
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

            if (IsStockFinanceQuery(originalQuery))
                items = NormalizeFinanceItems(originalQuery, items);

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

            var searchPayload = new SearchSummaryPayload
            {
                Query = query ?? "",
                Summary = answer ?? "",
                Items = new List<SearchSummaryItem>
                {
                    new SearchSummaryItem
                    {
                        Title = "Perplexity authoritative finance research",
                        KeyPoint = answer ?? "",
                        Source = "Perplexity Sonar",
                        Date = now
                    }
                }
            };

            var verifiedPayload = new VerifiedFactPayload
            {
                Query = query ?? "",
                Summary = answer ?? "",
                Facts = new List<VerifiedFactItem>
                {
                    new VerifiedFactItem
                    {
                        Subject = BuildVerifiedSubject(query),
                        FactType = "authoritative_finance_research",
                        Value = answer ?? "",
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

        private async Task<string> TryGenerateAuthoritativeResearchAsync(
            string query,
            CancellationToken ct)
        {
            string instructions =
@"你是金融資料研究代理。
請查詢最新可得資料，回答使用者要求。
若是股價，請明確列出 ticker、價格、交易時間或資料時間。
若是財報，請列出營收、EPS、毛利率、指引等可查證數據。
不要混用舊資料；若資料來源衝突，請明確分開說明。
請使用繁體中文，輸出乾淨可供後續模型整合的研究結果。";

            string prompt =
$@"使用者問題：
{query}

請輸出：
1. 最新股價 / 報價時間
2. 最新財報重點
3. 可查證的市場資訊
4. 若資料衝突，請明確列出衝突，不要自行合併。";

            string answer = await TryInvokeStringAsync(
                "GenerateAgentAsync",
                instructions,
                prompt,
                ct);

            if (!string.IsNullOrWhiteSpace(answer))
                return answer.Trim();

            answer = await TryInvokeStringAsync(
                "GenerateAsync",
                instructions,
                prompt,
                ct);

            if (!string.IsNullOrWhiteSpace(answer))
                return answer.Trim();

            return "";
        }

        private async Task<string> TryInvokeStringAsync(
            string methodName,
            string instructions,
            string prompt,
            CancellationToken ct)
        {
            try
            {
                var methods = _service
                    .GetType()
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .Where(x => string.Equals(x.Name, methodName, StringComparison.Ordinal))
                    .ToList();

                foreach (var method in methods)
                {
                    var parameters = method.GetParameters();

                    object?[]? args = TryBuildArguments(
                        parameters,
                        instructions,
                        prompt,
                        ct);

                    if (args == null)
                        continue;

                    object? raw = method.Invoke(_service, args);

                    if (raw is Task<string> taskString)
                        return await taskString.ConfigureAwait(false);

                    if (raw is Task task)
                    {
                        await task.ConfigureAwait(false);

                        var resultProp = task.GetType().GetProperty("Result");
                        var value = resultProp?.GetValue(task);
                        return value?.ToString() ?? "";
                    }

                    return raw?.ToString() ?? "";
                }
            }
            catch
            {
                return "";
            }

            return "";
        }

        private static object?[]? TryBuildArguments(
            ParameterInfo[] parameters,
            string instructions,
            string prompt,
            CancellationToken ct)
        {
            var args = new object?[parameters.Length];

            for (int i = 0; i < parameters.Length; i++)
            {
                var p = parameters[i];
                string name = p.Name ?? "";

                if (p.ParameterType == typeof(string))
                {
                    if (name.Contains("instruction", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("system", StringComparison.OrdinalIgnoreCase))
                    {
                        args[i] = instructions;
                    }
                    else
                    {
                        args[i] = prompt;
                    }

                    continue;
                }

                if (p.ParameterType == typeof(bool))
                {
                    if (name.Contains("web", StringComparison.OrdinalIgnoreCase) ||
                        name.Contains("search", StringComparison.OrdinalIgnoreCase))
                    {
                        args[i] = true;
                    }
                    else
                    {
                        args[i] = false;
                    }

                    continue;
                }

                if (p.ParameterType == typeof(int))
                {
                    args[i] = 4000;
                    continue;
                }

                if (p.ParameterType == typeof(CancellationToken))
                {
                    args[i] = ct;
                    continue;
                }

                if (p.HasDefaultValue)
                {
                    args[i] = p.DefaultValue;
                    continue;
                }

                return null;
            }

            return args;
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

            bool finance = IsStockFinanceQuery(query);

            var topItems = items
                .Take(finance ? 3 : 5)
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
                    FactType = finance ? "finance_quote_context" : "general",
                    Value = item.KeyPoint ?? "",
                    Unit = "",
                    AsOf = item.Date ?? "",
                    SourceTitle = item.Title ?? "",
                    SourceUrl = item.Source ?? "",
                    Confidence = finance ? "medium" : "high"
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

            if (!IsStockFinanceQuery(query))
                return query;

            var tickers = DetectTickers(query);
            if (tickers.Count == 0)
                return query;

            string tickerPart = string.Join(" ", tickers);

            return
                $"{tickerPart} latest stock price current quote latest earnings revenue EPS gross margin guidance short term outlook";
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

        private static List<SearchSummaryItem> NormalizeFinanceItems(
            string query,
            List<SearchSummaryItem> items)
        {
            if (items == null || items.Count == 0)
                return items ?? new List<SearchSummaryItem>();

            var tickers = DetectTickers(query);

            var scored = items
                .Select(x => new
                {
                    Item = x,
                    Score = ScoreFinanceItem(x, tickers)
                })
                .OrderByDescending(x => x.Score)
                .ThenByDescending(x => ParseDateScore(x.Item.Date))
                .ToList();

            var selected = scored
                .Where(x => x.Score > 0)
                .Select(x => x.Item)
                .Take(6)
                .ToList();

            if (selected.Count == 0)
                selected = scored.Select(x => x.Item).Take(6).ToList();

            return selected;
        }

        private static int ScoreFinanceItem(SearchSummaryItem item, IReadOnlyList<string> tickers)
        {
            if (item == null)
                return 0;

            string all = $"{item.Title} {item.KeyPoint} {item.Source} {item.Date}";
            int score = 0;

            foreach (var ticker in tickers ?? Array.Empty<string>())
            {
                if (all.Contains(ticker, StringComparison.OrdinalIgnoreCase))
                    score += 8;
            }

            if (ContainsAny(all, "latest", "current", "real-time", "quote", "stock price", "price", "after hours"))
                score += 6;

            if (ContainsAny(all, "earnings", "revenue", "EPS", "gross margin", "guidance", "財報", "營收", "毛利率"))
                score += 5;

            if (ContainsAny(all, "nasdaq.com", "marketwatch.com", "cnbc.com", "yahoo.com", "finance.yahoo.com", "marketwatch", "nasdaq"))
                score += 4;

            if (ContainsAny(all, "forecast", "prediction", "analyst", "target price"))
                score += 1;

            if (ContainsAny(all, "2024", "2023", "2022"))
                score -= 6;

            if (ContainsAny(all, "historical", "history", "all time", "52 week"))
                score -= 2;

            return score;
        }

        private static double ParseDateScore(string date)
        {
            if (string.IsNullOrWhiteSpace(date))
                return 0;

            return DateTime.TryParse(date, out var parsed)
                ? parsed.ToOADate()
                : 0;
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

        private static string BuildSummary(string query, IEnumerable<SearchSummaryItem> items)
        {
            var list = items?
                .Where(x => x != null)
                .ToList() ?? new List<SearchSummaryItem>();

            if (list.Count == 0)
                return "無明確重點資訊";

            var sb = new StringBuilder();

            if (IsStockFinanceQuery(query))
            {
                sb.AppendLine("整理重點如下：");
                sb.AppendLine("此摘要來自搜尋片段，可信度低於 Perplexity authoritative finance research；若有 verified_facts，最終回答必須以 verified_facts 為準。");
            }
            else
            {
                sb.AppendLine("整理重點如下：");
            }

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
    }
}