using System;
using System.Collections.Generic;

namespace test
{
    /// <summary>
    /// Product UX v1：Token / 成本「估算」。
    /// 目前底層串流服務沒有回傳 API 的 usage 區塊，因此這裡用字元數推估 token，
    /// 再依各模型每百萬 token 價目表估算成本。所有顯示都標明「估算」，
    /// 之後若把真實 usage 串回來，只要改 Estimate 的來源即可，呼叫端不用動。
    /// </summary>
    public static class ModelCostEstimator
    {
        public readonly struct Estimate
        {
            public Estimate(int inputTokens, int outputTokens, double usdCost, bool isActual = false)
            {
                InputTokens = inputTokens;
                OutputTokens = outputTokens;
                UsdCost = usdCost;
                IsActual = isActual;
            }

            public int InputTokens { get; }
            public int OutputTokens { get; }
            public int TotalTokens => InputTokens + OutputTokens;
            public double UsdCost { get; }

            // 是否為 API 回傳的真實用量（true）或字元數推估（false）。
            public bool IsActual { get; }

            // 真實：「實際 2.4k tokens · 約 NT$0.6」；估算：「估算 ≈ 2.4k tokens · 約 NT$0.6」。
            // token 數標「實際」，金額仍標「約」（價目表與匯率本身是近似值）。
            public string Display => IsActual
                ? $"實際 {FormatTokens(TotalTokens)} tokens · 約 {FormatTwd(UsdCost)}"
                : $"估算 ≈ {FormatTokens(TotalTokens)} tokens · 約 {FormatTwd(UsdCost)}";
        }

        // 每百萬 token 美元單價（input / output）。皆為估算值，僅供成本量級參考。
        private readonly struct Price
        {
            public Price(double inputPerMillion, double outputPerMillion)
            {
                InputPerMillion = inputPerMillion;
                OutputPerMillion = outputPerMillion;
            }

            public double InputPerMillion { get; }
            public double OutputPerMillion { get; }
        }

        private static readonly Price DefaultPrice = new(3.00, 15.00);

        private static readonly Dictionary<string, Price> PriceTable = new(StringComparer.OrdinalIgnoreCase)
        {
            ["gpt-5.5"] = new Price(2.50, 10.00),
            ["claude-sonnet-4-6"] = new Price(3.00, 15.00),
            ["claude-opus-4-8"] = new Price(15.00, 75.00),
            ["pplx-sonar"] = new Price(1.00, 1.00),
            ["pplx-sonar-deep-research"] = new Price(2.00, 8.00),
            ["gemini-3.1-pro"] = new Price(1.25, 10.00),
            ["gemini-3.5-flash"] = new Price(1.50, 9.00),
        };

        // 約略匯率，僅用於估算顯示。
        private const double UsdToTwd = 32.0;

        public static Estimate Compute(string? modelId, string? inputText, string? outputText)
        {
            int inputTokens = EstimateTokens(inputText);
            int outputTokens = EstimateTokens(outputText);

            return BuildEstimate(modelId, inputTokens, outputTokens, isActual: false);
        }

        /// <summary>用 API 回傳的真實 token 數計算成本（標示為「實際」）。</summary>
        public static Estimate FromUsage(string? modelId, int inputTokens, int outputTokens)
        {
            return BuildEstimate(modelId, Math.Max(0, inputTokens), Math.Max(0, outputTokens), isActual: true);
        }

        private static Estimate BuildEstimate(string? modelId, int inputTokens, int outputTokens, bool isActual)
        {
            Price price = ResolvePrice(modelId);

            double usd =
                inputTokens / 1_000_000.0 * price.InputPerMillion +
                outputTokens / 1_000_000.0 * price.OutputPerMillion;

            return new Estimate(inputTokens, outputTokens, usd, isActual);
        }

        /// <summary>
        /// 粗估 token 數：CJK 字元約 1.5 字/token，其餘（拉丁、數字、符號）約 4 字/token。
        /// </summary>
        public static int EstimateTokens(string? text)
        {
            if (string.IsNullOrEmpty(text))
                return 0;

            int cjk = 0;
            int other = 0;

            foreach (char c in text)
            {
                if (IsCjk(c))
                    cjk++;
                else if (!char.IsWhiteSpace(c))
                    other++;
            }

            double tokens = (cjk / 1.5) + (other / 4.0);
            return (int)Math.Ceiling(tokens);
        }

        private static bool IsCjk(char c)
        {
            return
                (c >= '一' && c <= '鿿') ||   // CJK 統一表意文字
                (c >= '㐀' && c <= '䶿') ||   // 擴充 A
                (c >= '぀' && c <= 'ヿ') ||   // 平假名 / 片假名
                (c >= '＀' && c <= '￯');     // 全形符號
        }

        private static Price ResolvePrice(string? modelId)
        {
            if (!string.IsNullOrWhiteSpace(modelId) &&
                PriceTable.TryGetValue(modelId.Trim(), out var p))
            {
                return p;
            }

            return DefaultPrice;
        }

        private static string FormatTokens(int tokens)
        {
            if (tokens >= 1000)
                return (tokens / 1000.0).ToString("0.0") + "k";

            return tokens.ToString();
        }

        private static string FormatTwd(double usd)
        {
            double twd = usd * UsdToTwd;

            if (twd < 0.01)
                return "< NT$0.01";

            return "NT$" + twd.ToString("0.00");
        }
    }
}
