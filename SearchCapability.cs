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
2. 必須分開 latest tradable/current session price、regular close、after-hours、pre-market。regular close、after-hours、pre-market、regular session intraday 是不同交易時段，不是資料衝突。
3. 每個 ticker 至少嘗試取得：latest tradable price/current session、regular close price、after-hours/pre-market price、price time、latest fiscal quarter、revenue、EPS、gross margin、guidance。
4. 財報數字請優先使用公司 IR / earnings release / SEC filing。若公司官方資料可取得，不可寫「財報核心數字不足」。
5. 若 ticker 是 MU，請優先查 Micron Investor Relations / Micron earnings release 的最新季度資料；若 ticker 是 TSM，請優先查 TSMC Investor Relations / quarterly results 的最新季度資料。
6. Revenue、EPS、Gross Margin 只能從官方 earnings release / quarterly results 的「Quarterly Financial Results」或等價正式表格抽取；不可使用新聞摘要、股價頁、分析文章、預估文或搜尋摘要中的二手數字作為 primary fact。
7. Guidance 只能從官方 business outlook / guidance / outlook table 抽取；不可把下一季 guidance 寫成最新一季 revenue。
8. 若官方表格標示 in millions，請精確換算：例如 Revenue $23,860 million 必須寫 US$23.86B；不可改成 US$30.8B 或其他估算值。
9. EPS 必須明確標示 GAAP 或 non-GAAP；若兩者皆取得，請在 EPS 欄同時列出，例如 GAAP EPS: US$12.07; Non-GAAP EPS: US$12.20。
10. 如果 Official Earnings Source 已取得，但 Revenue、EPS、Gross Margin 或 Guidance 仍是「未取得」，請先再次查官方財報來源，不要直接宣稱財報核心數字不足。
11. 不要把歷史價格、目標價、預測價、不同 ticker、不同年度或不同季度混在一起。
12. 只有在同一 ticker、同一 fact type、同一交易時段或同一財報欄位存在不可判斷的重大差異時，才列「資料衝突」。
13. 若某個價格明顯像歷史價、目標價、錯誤映射、不同日期舊資料，請不要列入 primary facts，只能列入 ignored notes。
14. 若目前非盤前交易時段，Pre Market Price 可以寫「未取得 / 非盤前時段」；若資料源沒有真正即時價，Latest Tradable Price 寫「未取得」，不可用 regular close 假裝即時價。
15. 不要自己發明數字。
16. 不要使用 GPT 內部知識補資料。
17. 若找不到某欄位，該欄位寫「未取得」，不要整體回答資料不足。
18. 請輸出乾淨、短而結構化的繁體中文結果，供後續 final synthesizer 使用。

官方財報抽取自我檢查：
- MU / Micron：若最新官方資料是 FQ2-26，Revenue 應來自 total company Revenue row，不可使用 business unit revenue 或 FQ3 guidance 當作 FQ2 revenue。
- TSM / TSMC：若最新官方資料是 Q1 2026，Revenue 應來自 consolidated revenue row，Gross Margin 應來自 gross margin for the quarter。
- 輸出前請逐欄確認 Revenue、EPS、Gross Margin、Guidance 的來源類型是 official earnings，不是 quote provider。

請嚴格使用以下格式：

【Primary Facts】
Ticker: 
Company:
Latest Tradable Price:
Latest Tradable Time:
Latest Tradable Session:
Regular Close Price:
Regular Close Time:
After Hours Price:
After Hours Time:
Pre Market Price:
Pre Market Time:
Latest Fiscal Quarter:
Revenue:
EPS:
Gross Margin:
Guidance:
Key Market Drivers:
Official Earnings Source:
Quote Source:

Ticker:
Company:
Latest Tradable Price:
Latest Tradable Time:
Latest Tradable Session:
Regular Close Price:
Regular Close Time:
After Hours Price:
After Hours Time:
Pre Market Price:
Pre Market Time:
Latest Fiscal Quarter:
Revenue:
EPS:
Gross Margin:
Guidance:
Key Market Drivers:
Official Earnings Source:
Quote Source:

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
                maxOutputTokens: 4000,
                ct: ct);

            if (string.IsNullOrWhiteSpace(answer))
            {
                return AgentCapabilityResult.DirectHandle(
                    "search-capability required authoritative finance research, but Perplexity returned empty result.");
            }

            return await BuildAuthoritativeFinanceResultAsync(
                originalQuery,
                answer,
                ct);
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

        private async Task<AgentCapabilityResult> BuildAuthoritativeFinanceResultAsync(
            string query,
            string answer,
            CancellationToken ct)
        {
            string now = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss 'UTC'");

            string cleanedAnswer = CleanFinanceResearchAnswer(answer);
            var facts = BuildFinanceVerifiedFacts(query ?? "", cleanedAnswer, now);
            string combinedAnswer = cleanedAnswer;

            var officialFactRequests = BuildOfficialFinanceFactRequests(query ?? "");
            if (officialFactRequests.Count > 0)
            {
                string repairAnswer = await ExecuteFinanceFactRepairAsync(
                    query ?? "",
                    cleanedAnswer,
                    officialFactRequests,
                    ct);

                if (!string.IsNullOrWhiteSpace(repairAnswer))
                {
                    string cleanedRepair = CleanFinanceResearchAnswer(repairAnswer);
                    var repairedFacts = BuildFinanceVerifiedFacts(query ?? "", cleanedRepair, now);

                    facts = MergeFinanceFacts(facts, repairedFacts);
                    combinedAnswer =
                        "【Official Facts Repair Applied】" +
                        Environment.NewLine +
                        "上一輪研究缺少部分官方財報欄位；以下已用官方財報 repair pass 補齊。下游只能使用 verified_facts 中的結構化欄位，不應再把第一輪未校正資料視為衝突來源。" +
                        Environment.NewLine +
                        cleanedRepair;
                }
            }

            var quoteFactRequests = BuildMissingQuoteFactRequests(facts, query ?? "");
            if (quoteFactRequests.Count > 0)
            {
                string quoteRepairAnswer = await ExecuteFinanceQuoteRepairAsync(
                    query ?? "",
                    cleanedAnswer,
                    quoteFactRequests,
                    ct);

                if (!string.IsNullOrWhiteSpace(quoteRepairAnswer))
                {
                    string cleanedQuoteRepair = CleanFinanceResearchAnswer(quoteRepairAnswer);
                    var quoteFacts = BuildFinanceVerifiedFacts(query ?? "", cleanedQuoteRepair, now);

                    facts = MergeFinanceFacts(facts, quoteFacts);
                    combinedAnswer +=
                        Environment.NewLine +
                        "【Quote Repair Applied】" +
                        Environment.NewLine +
                        "缺失的報價欄位已用 quote-only repair pass 補查。";
                }
            }

            var searchPayload = new SearchSummaryPayload
            {
                Query = query ?? "",
                Summary = BuildFinanceSearchContextSummary(facts),
                Items = new List<SearchSummaryItem>
                {
                    new SearchSummaryItem
                    {
                        Title = "Perplexity authoritative finance research",
                        KeyPoint = BuildFinanceSearchContextSummary(facts),
                        Source = "Perplexity Sonar",
                        Date = now
                    }
                }
            };

            var verifiedPayload = new VerifiedFactPayload
            {
                Query = query ?? "",
                Summary = BuildFinanceFactSummary(facts, combinedAnswer),
                Facts = facts
            };

            var result = AgentCapabilityResult.WithData("verified_facts", verifiedPayload);
            result.Data["search_summary"] = searchPayload;

            return result;
        }

        private static IReadOnlyList<VerifiedFactItem> BuildFinanceVerifiedFacts(
            string query,
            string cleanedAnswer,
            string now)
        {
            var facts = new List<VerifiedFactItem>();
            var blocks = ExtractPrimaryFactBlocks(cleanedAnswer);

            foreach (var block in blocks)
            {
                string ticker = ReadFinanceField(block, "Ticker");
                if (string.IsNullOrWhiteSpace(ticker))
                    continue;

                string company = ReadFinanceField(block, "Company");
                string subject = string.IsNullOrWhiteSpace(company)
                    ? ticker.Trim()
                    : $"{ticker.Trim()} ({company.Trim()})";

                string regularClosePrice = ReadFinanceField(block, "Regular Close Price", "Regular Close", "Close Price");
                string regularCloseTime = ReadFinanceField(block, "Regular Close Time", "Close Time", "Price Time");
                string afterHoursPrice = ReadFinanceField(block, "After Hours Price", "After-hours Price", "Afterhours Price");
                string afterHoursTime = ReadFinanceField(block, "After Hours Time", "After-hours Time", "Afterhours Time");
                string preMarketPrice = ReadFinanceField(block, "Pre Market Price", "Pre-market Price", "Premarket Price");
                string preMarketTime = ReadFinanceField(block, "Pre Market Time", "Pre-market Time", "Premarket Time");

                AddFinanceFact(facts, subject, "regular_close_price", regularClosePrice, "USD", regularCloseTime, now);
                AddFinanceFact(facts, subject, "after_hours_price", afterHoursPrice, "USD", afterHoursTime, now);
                AddFinanceFact(facts, subject, "pre_market_price", preMarketPrice, "USD", preMarketTime, now);
                AddFinanceFact(facts, subject, "latest_fiscal_quarter", ReadFinanceField(block, "Latest Fiscal Quarter", "Fiscal Quarter", "Quarter"), "", now, now);
                AddFinanceFact(facts, subject, "revenue", ReadFinanceField(block, "Revenue", "Revenue / Sales", "Net Sales"), "", now, now);
                AddFinanceFact(facts, subject, "eps", ReadFinanceField(block, "EPS", "Earnings Per Share", "Non-GAAP EPS", "GAAP EPS"), "", now, now);
                AddFinanceFact(facts, subject, "gross_margin", ReadFinanceField(block, "Gross Margin", "Non-GAAP Gross Margin", "GAAP Gross Margin"), "", now, now);
                AddFinanceFact(facts, subject, "guidance", ReadFinanceField(block, "Guidance", "Outlook", "Forecast"), "", now, now);
                AddFinanceFact(facts, subject, "key_market_drivers", ReadFinanceField(block, "Key Market Drivers", "Market Drivers", "Drivers"), "", now, now);
                AddFinanceFact(facts, subject, "official_earnings_source", ReadFinanceField(block, "Official Earnings Source", "Earnings Source", "Official Source"), "", now, now);
                AddFinanceFact(facts, subject, "quote_source", ReadFinanceField(block, "Quote Source", "Price Source"), "", now, now);
                AddQuoteAvailabilityFact(facts, subject, regularClosePrice, afterHoursPrice, preMarketPrice, now);
            }

            if (facts.Count > 0)
                return facts;

            return new List<VerifiedFactItem>
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
                    Confidence = "medium",
                    OwnerAgentId = FactOwnership.ResearchAgent,
                    OwnerCapabilityId = FactOwnership.SearchCapability,
                    AuthorityLevel = FactOwnership.AuthoritySearchContext,
                    UsageRole = FactOwnership.UsageBackgroundContext
                }
            };
        }

        private async Task<string> ExecuteFinanceFactRepairAsync(
            string query,
            string firstAnswer,
            IReadOnlyList<string> missingFacts,
            CancellationToken ct)
        {
            if (missingFacts == null || missingFacts.Count == 0)
                return "";

            string tickers = string.Join(", ", DetectTickers(query));
            string requestedFacts = string.Join(Environment.NewLine, missingFacts.Select(x => "- " + x));

            string instructions =
$@"你是金融資料校正代理。上一輪研究可能包含不完整或錯口徑的財報資料。請重新用官方來源校正下列財報欄位，不要寫完整分析。

使用者問題：
{query}

目標 ticker：
{tickers}

需要官方校正的欄位：
{requestedFacts}

上一輪輸出：
{firstAnswer}

硬性規則：
1. 只能使用公司官方 IR / earnings release / quarterly results / SEC filing。
2. Revenue、EPS、Gross Margin 必須從最新季度正式財報表格抽取。
3. Guidance 必須從官方 business outlook / guidance / outlook table 抽取。
4. 不可使用新聞摘要、股價頁、分析文章、預估文或搜尋摘要的二手數字。
5. 若官方表格標示 in millions，請精確換算：Revenue $23,860 million = US$23.86B。
6. EPS 必須標示 GAAP 或 non-GAAP；若兩者皆取得，請同時列出。
7. 不要把下一季 guidance 寫成最新一季 revenue。
8. 不要自己發明數字；若官方來源仍不可取得，才寫「未取得」。

請嚴格輸出以下格式，只輸出官方校正後的財報欄位：

【Primary Facts】
Ticker:
Company:
Latest Fiscal Quarter:
Revenue:
EPS:
Gross Margin:
Guidance:
Official Earnings Source:

Ticker:
Company:
Latest Fiscal Quarter:
Revenue:
EPS:
Gross Margin:
Guidance:
Official Earnings Source:

【Conflicts】
若沒有，寫：無重大衝突。

【Ignored / Low Confidence】
若沒有，寫：無。";

            return await _service.GenerateAgentAsync(
                instructions: instructions,
                userText: query,
                enableWebSearch: true,
                maxOutputTokens: 2500,
                ct: ct);
        }

        private static IReadOnlyList<string> BuildOfficialFinanceFactRequests(
            string query)
        {
            var requests = new List<string>();
            var tickers = DetectTickers(query);

            if (tickers.Count == 0)
                return requests;

            string[] requiredFactTypes =
            {
                "latest_fiscal_quarter",
                "revenue",
                "eps",
                "gross_margin",
                "guidance",
                "official_earnings_source"
            };

            foreach (var ticker in tickers)
            {
                foreach (var factType in requiredFactTypes)
                {
                    requests.Add($"{ticker}: {factType}");
                }
            }

            return requests;
        }

        private async Task<string> ExecuteFinanceQuoteRepairAsync(
            string query,
            string firstAnswer,
            IReadOnlyList<string> missingQuotes,
            CancellationToken ct)
        {
            if (missingQuotes == null || missingQuotes.Count == 0)
                return "";

            string tickers = string.Join(", ", DetectTickers(query));
            string requestedQuotes = string.Join(Environment.NewLine, missingQuotes.Select(x => "- " + x));

            string instructions =
$@"你是美股報價校正代理。請只補查缺失的報價欄位，不要重新分析財報，不要輸出完整投資分析。

使用者問題：
{query}

目標 ticker：
{tickers}

需要補查的報價欄位：
{requestedQuotes}

上一輪輸出：
{firstAnswer}

硬性規則：
1. 優先使用 quote card、交易所、券商、Yahoo Finance、Nasdaq、MarketWatch、CNBC、Google Finance 類型資料。
2. Regular Close Price 是最新正常交易時段收盤價。
3. After Hours Price 是盤後價；若資料源沒有提供，寫「未取得」。
4. Pre Market Price 是盤前價；若目前不是盤前時段或資料源沒有提供，寫「未取得 / 非盤前時段」。
5. 不可把歷史價、目標價、52 週高低、不同 ticker、不同日期舊價當成最新報價。
6. 不可把 regular close 假裝 after-hours、pre-market 或 realtime。
7. 若找不到某欄位，只寫「未取得」，不要刪掉其它已取得欄位。

請嚴格輸出以下格式：

【Primary Facts】
Ticker:
Company:
Regular Close Price:
Regular Close Time:
After Hours Price:
After Hours Time:
Pre Market Price:
Pre Market Time:
Quote Source:

Ticker:
Company:
Regular Close Price:
Regular Close Time:
After Hours Price:
After Hours Time:
Pre Market Price:
Pre Market Time:
Quote Source:

【Conflicts】
若沒有，寫：無重大衝突。

【Ignored / Low Confidence】
若沒有，寫：無。";

            return await _service.GenerateAgentAsync(
                instructions: instructions,
                userText: query,
                enableWebSearch: true,
                maxOutputTokens: 1800,
                ct: ct);
        }

        private static IReadOnlyList<string> BuildMissingQuoteFactRequests(
            IReadOnlyList<VerifiedFactItem> facts,
            string query)
        {
            var requests = new List<string>();
            var tickers = DetectTickers(query);

            if (tickers.Count == 0)
                return requests;

            string[] quoteFactTypes =
            {
                "regular_close_price",
                "after_hours_price",
                "pre_market_price"
            };

            foreach (var ticker in tickers)
            {
                foreach (var factType in quoteFactTypes)
                {
                    if (!HasFinanceFact(facts, ticker, factType))
                        requests.Add($"{ticker}: {factType}");
                }
            }

            return requests;
        }

        private static bool HasFinanceFact(
            IReadOnlyList<VerifiedFactItem> facts,
            string ticker,
            string factType)
        {
            if (facts == null ||
                string.IsNullOrWhiteSpace(ticker) ||
                string.IsNullOrWhiteSpace(factType))
            {
                return false;
            }

            return facts.Any(x =>
                x != null &&
                SubjectMatchesTicker(x.Subject, ticker) &&
                string.Equals(x.FactType, factType, StringComparison.OrdinalIgnoreCase) &&
                !IsMissingFinanceValue(x.Value));
        }

        private static bool SubjectMatchesTicker(string subject, string ticker)
        {
            if (string.IsNullOrWhiteSpace(subject) ||
                string.IsNullOrWhiteSpace(ticker))
            {
                return false;
            }

            subject = subject.Trim();
            ticker = ticker.Trim();

            return string.Equals(subject, ticker, StringComparison.OrdinalIgnoreCase) ||
                   subject.StartsWith(ticker + " ", StringComparison.OrdinalIgnoreCase) ||
                   subject.StartsWith(ticker + "(", StringComparison.OrdinalIgnoreCase);
        }

        private static IReadOnlyList<VerifiedFactItem> MergeFinanceFacts(
            IReadOnlyList<VerifiedFactItem> primaryFacts,
            IReadOnlyList<VerifiedFactItem> repairedFacts)
        {
            var merged = new List<VerifiedFactItem>();

            if (primaryFacts != null)
                merged.AddRange(primaryFacts.Where(x => x != null));

            if (repairedFacts == null || repairedFacts.Count == 0)
                return merged;

            foreach (var repaired in repairedFacts.Where(x => x != null))
            {
                int existingIndex = merged.FindIndex(x =>
                    string.Equals(NormalizeSubjectForMerge(x.Subject), NormalizeSubjectForMerge(repaired.Subject), StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.FactType, repaired.FactType, StringComparison.OrdinalIgnoreCase));

                if (existingIndex >= 0)
                {
                    if (ShouldReplaceFinanceFact(merged[existingIndex], repaired))
                        merged[existingIndex] = repaired;
                }
                else
                {
                    merged.Add(repaired);
                }
            }

            return merged;
        }

        private static bool ShouldReplaceFinanceFact(
            VerifiedFactItem current,
            VerifiedFactItem repaired)
        {
            if (current == null)
                return true;

            if (repaired == null || IsMissingFinanceValue(repaired.Value))
                return false;

            if (IsMissingFinanceValue(current.Value))
                return true;

            if (string.Equals(repaired.FactType, "quote_availability", StringComparison.OrdinalIgnoreCase))
                return true;

            if (IsOfficialFinanceMetric(repaired.FactType))
                return true;

            if (FactOwnership.CanOwnNumericFacts(current) &&
                !FactOwnership.CanOwnNumericFacts(repaired))
            {
                return false;
            }

            if (!FactOwnership.CanOwnNumericFacts(current) &&
                FactOwnership.CanOwnNumericFacts(repaired))
            {
                return true;
            }

            int repairedAuthority = FactOwnership.AuthorityRank(repaired.AuthorityLevel);
            int currentAuthority = FactOwnership.AuthorityRank(current.AuthorityLevel);

            if (repairedAuthority != currentAuthority)
                return repairedAuthority > currentAuthority;

            bool repairedOfficial =
                (repaired.SourceTitle ?? "").Contains("official", StringComparison.OrdinalIgnoreCase) ||
                (repaired.SourceTitle ?? "").Contains("Perplexity Sonar finance research", StringComparison.OrdinalIgnoreCase);

            bool currentOfficial =
                (current.SourceTitle ?? "").Contains("official", StringComparison.OrdinalIgnoreCase) ||
                (current.SourceTitle ?? "").Contains("Perplexity Sonar finance research", StringComparison.OrdinalIgnoreCase);

            return repairedOfficial && !currentOfficial;
        }

        private static bool IsOfficialFinanceMetric(string factType)
        {
            return string.Equals(factType, "latest_fiscal_quarter", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "revenue", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "eps", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "gross_margin", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "guidance", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(factType, "official_earnings_source", StringComparison.OrdinalIgnoreCase);
        }

        private static string NormalizeSubjectForMerge(string subject)
        {
            if (string.IsNullOrWhiteSpace(subject))
                return "";

            string trimmed = subject.Trim();
            int spaceIndex = trimmed.IndexOf(' ');
            int parenIndex = trimmed.IndexOf('(');

            int cut = -1;
            if (spaceIndex > 0)
                cut = spaceIndex;
            if (parenIndex > 0 && (cut < 0 || parenIndex < cut))
                cut = parenIndex;

            return cut > 0
                ? trimmed.Substring(0, cut)
                : trimmed;
        }

        private static string BuildFinanceFactSummary(
            IReadOnlyList<VerifiedFactItem> facts,
            string cleanedAnswer)
        {
            if (facts == null || facts.Count == 0)
                return cleanedAnswer ?? "";

            var subjects = facts
                .Select(x => x.Subject)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            return
                "金融研究已轉成結構化 verified_facts。regular_close_price、after_hours_price、pre_market_price 屬於不同交易時段，不可互相視為資料衝突。"
                + Environment.NewLine
                + $"Subjects: {string.Join(", ", subjects)}";
        }

        private static string BuildFinanceSearchContextSummary(
            IReadOnlyList<VerifiedFactItem> facts)
        {
            var subjects = facts?
                .Where(x => x != null)
                .Select(x => x.Subject)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList() ?? new List<string>();

            string subjectText = subjects.Count == 0
                ? "unknown subjects"
                : string.Join(", ", subjects);

            return
                "Finance research produced structured verified_facts for " +
                subjectText +
                ". Numeric details are intentionally omitted from search_summary; downstream answers must use verified_facts for prices, dates, revenue, EPS, gross margin and guidance.";
        }

        private static IReadOnlyList<List<string>> ExtractPrimaryFactBlocks(string text)
        {
            var blocks = new List<List<string>>();
            var current = new List<string>();
            bool inPrimaryFacts = false;

            foreach (var rawLine in SplitLines(text))
            {
                string line = NormalizeFinanceLine(rawLine);

                if (line.Equals("【Primary Facts】", StringComparison.OrdinalIgnoreCase))
                {
                    inPrimaryFacts = true;
                    continue;
                }

                if (inPrimaryFacts && line.StartsWith("【", StringComparison.Ordinal))
                    break;

                if (!inPrimaryFacts || string.IsNullOrWhiteSpace(line))
                    continue;

                if (line.StartsWith("Ticker:", StringComparison.OrdinalIgnoreCase) &&
                    current.Count > 0)
                {
                    blocks.Add(current);
                    current = new List<string>();
                }

                current.Add(line);
            }

            if (current.Count > 0)
                blocks.Add(current);

            return blocks;
        }

        private static IEnumerable<string> SplitLines(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return Array.Empty<string>();

            return text
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n');
        }

        private static string NormalizeFinanceLine(string line)
        {
            if (string.IsNullOrWhiteSpace(line))
                return "";

            line = line.Trim();

            line = line
                .Replace("：", ":")
                .Replace("__", "")
                .Replace("**", "");

            while (line.StartsWith("-", StringComparison.Ordinal) ||
                   line.StartsWith("*", StringComparison.Ordinal) ||
                   line.StartsWith("•", StringComparison.Ordinal))
            {
                line = line.Substring(1).TrimStart();
            }

            line = line.Trim('*', ' ', '\t');

            return line.Trim();
        }

        private static string ReadFinanceField(
            IReadOnlyList<string> block,
            params string[] fieldNames)
        {
            if (block == null || fieldNames == null || fieldNames.Length == 0)
                return "";

            foreach (var fieldName in fieldNames)
            {
                if (string.IsNullOrWhiteSpace(fieldName))
                    continue;

                string prefix = NormalizeFinanceLabel(fieldName) + ":";

                var line = block.FirstOrDefault(x =>
                    NormalizeFinanceLabel(x).StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

                if (string.IsNullOrWhiteSpace(line))
                    continue;

                int colonIndex = line.IndexOf(':');
                if (colonIndex < 0 || colonIndex >= line.Length - 1)
                    return "";

                return CleanFinanceValue(line.Substring(colonIndex + 1));
            }

            return "";
        }

        private static string NormalizeFinanceLabel(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            return text
                .Replace("：", ":")
                .Replace("__", "")
                .Replace("**", "")
                .Trim()
                .ToLowerInvariant();
        }

        private static string CleanFinanceValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            return value
                .Replace("**", "")
                .Replace("__", "")
                .Trim()
                .Trim('*', ' ', '\t');
        }

        private static void AddFinanceFact(
            List<VerifiedFactItem> facts,
            string subject,
            string factType,
            string value,
            string unit,
            string asOf,
            string fallbackAsOf)
        {
            if (facts == null ||
                string.IsNullOrWhiteSpace(subject) ||
                string.IsNullOrWhiteSpace(factType) ||
                IsMissingFinanceValue(value))
            {
                return;
            }

            facts.Add(new VerifiedFactItem
            {
                Subject = subject.Trim(),
                FactType = factType.Trim(),
                Value = value.Trim(),
                Unit = unit ?? "",
                AsOf = string.IsNullOrWhiteSpace(asOf) ? fallbackAsOf : asOf.Trim(),
                SourceTitle = "Perplexity Sonar finance research",
                SourceUrl = "",
                Confidence = "high",
                OwnerAgentId = FactOwnership.ResearchAgent,
                OwnerCapabilityId = FactOwnership.SearchCapability,
                AuthorityLevel = ResolveFinanceAuthority(factType),
                UsageRole = FactOwnership.ResolveFinanceUsageRole(factType)
            });
        }

        private static void AddQuoteAvailabilityFact(
            List<VerifiedFactItem> facts,
            string subject,
            string regularClosePrice,
            string afterHoursPrice,
            string preMarketPrice,
            string now)
        {
            if (facts == null || string.IsNullOrWhiteSpace(subject))
                return;

            string value =
                "regular_close_price=" + AvailabilityLabel(regularClosePrice) +
                "; after_hours_price=" + AvailabilityLabel(afterHoursPrice) +
                "; pre_market_price=" + AvailabilityLabel(preMarketPrice) +
                "; realtime_price=not_available";

            facts.Add(new VerifiedFactItem
            {
                Subject = subject.Trim(),
                FactType = "quote_availability",
                Value = value,
                Unit = "",
                AsOf = now,
                SourceTitle = "Perplexity Sonar finance research",
                SourceUrl = "",
                Confidence = "high",
                OwnerAgentId = FactOwnership.ResearchAgent,
                OwnerCapabilityId = FactOwnership.SearchCapability,
                AuthorityLevel = FactOwnership.AuthorityMarketQuote,
                UsageRole = FactOwnership.UsageBackgroundContext
            });
        }

        private static string AvailabilityLabel(string value)
        {
            return IsMissingFinanceValue(value)
                ? "not_available"
                : "available";
        }

        private static string ResolveFinanceAuthority(string factType)
        {
            if (string.IsNullOrWhiteSpace(factType))
                return FactOwnership.AuthoritySearchContext;

            if (factType.Contains("price", StringComparison.OrdinalIgnoreCase) ||
                factType.Contains("quote", StringComparison.OrdinalIgnoreCase))
            {
                return FactOwnership.AuthorityMarketQuote;
            }

            if (string.Equals(factType, "key_market_drivers", StringComparison.OrdinalIgnoreCase))
                return FactOwnership.AuthoritySearchContext;

            return FactOwnership.AuthorityOfficial;
        }

        private static bool IsMissingFinanceValue(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return true;

            string normalized = value.Trim();
            return string.Equals(normalized, "未取得", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "N/A", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "NA", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalized, "-", StringComparison.OrdinalIgnoreCase);
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
                    Confidence = "medium",
                    OwnerAgentId = FactOwnership.ResearchAgent,
                    OwnerCapabilityId = FactOwnership.SearchCapability,
                    AuthorityLevel = FactOwnership.AuthoritySearchContext,
                    UsageRole = FactOwnership.UsageBackgroundContext
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
