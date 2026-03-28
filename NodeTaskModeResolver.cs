using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public sealed class NodeTaskModeResolution
    {
        public NodeTaskMode Mode { get; init; } = NodeTaskMode.Chat;
        public string Reason { get; init; } = "";
        public double Confidence { get; init; }
        public IReadOnlyList<string> MatchedKeywords { get; init; } = Array.Empty<string>();
    }

    public static class NodeTaskModeResolver
    {
        private sealed class Rule
        {
            public NodeTaskMode Mode { get; init; }
            public string Reason { get; init; } = "";
            public string[] Keywords { get; init; } = Array.Empty<string>();
            public double Confidence { get; init; } = 0.75;
        }

        private static readonly Rule[] _rules =
        {
            new Rule
            {
                Mode = NodeTaskMode.Translate,
                Reason = "偵測到翻譯/語言轉換需求",
                Confidence = 0.96,
                Keywords = new[]
                {
                    "翻譯","譯成","翻成","中文","英文","日文","韓文","對照","中英對照","完整中文菜單",
                    "translate","translation","menu translation","traditional chinese","繁體中文"
                }
            },
            new Rule
            {
                Mode = NodeTaskMode.Code,
                Reason = "偵測到程式/除錯/開發需求",
                Confidence = 0.95,
                Keywords = new[]
                {
                    "程式","程式碼","code","bug","錯誤","修正","debug","exception","class","method",
                    "c#","xaml",".net","wpf","visual studio","compile","build","namespace","完整程式",
                    "完整程式碼","可直接貼上","貼上即用"
                }
            },
            new Rule
            {
                Mode = NodeTaskMode.Research,
                Reason = "偵測到查證/搜尋/最新資訊需求",
                Confidence = 0.90,
                Keywords = new[]
                {
                    "查詢","搜尋","查證","最新","最近","新聞","資料來源","來源","比較","分析",
                    "research","search","latest","news","current","today","compare","source","citation"
                }
            },
            new Rule
            {
                Mode = NodeTaskMode.Summarize,
                Reason = "偵測到摘要/整理重點需求",
                Confidence = 0.88,
                Keywords = new[]
                {
                    "摘要","總結","整理重點","重點整理","濃縮","簡述","懶人包",
                    "summarize","summary","key points","tldr"
                }
            },
            new Rule
            {
                Mode = NodeTaskMode.Rewrite,
                Reason = "偵測到改寫/潤稿需求",
                Confidence = 0.86,
                Keywords = new[]
                {
                    "改寫","重寫","潤稿","修飾","順一下","口語化","正式一點","換個說法",
                    "rewrite","rephrase","polish","refine"
                }
            },
            new Rule
            {
                Mode = NodeTaskMode.Extract,
                Reason = "偵測到擷取/抽取結構化資訊需求",
                Confidence = 0.84,
                Keywords = new[]
                {
                    "擷取","抽取","提取","整理成表格","欄位","抓出","抽出","列出所有",
                    "extract","parse","fields","structured data"
                }
            }
        };

        public static NodeTaskModeResolution Resolve(string? text)
        {
            if (string.IsNullOrWhiteSpace(text))
            {
                return new NodeTaskModeResolution
                {
                    Mode = NodeTaskMode.Chat,
                    Reason = "內容為空，使用預設 Chat",
                    Confidence = 0.20,
                    MatchedKeywords = Array.Empty<string>()
                };
            }

            string raw = text.Trim();
            string normalized = raw.ToLowerInvariant();

            var matched = new List<(Rule Rule, List<string> Hits)>();

            foreach (var rule in _rules)
            {
                var hits = new List<string>();

                foreach (var keyword in rule.Keywords)
                {
                    if (string.IsNullOrWhiteSpace(keyword))
                        continue;

                    var k = keyword.Trim();
                    if (raw.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                        normalized.Contains(k.ToLowerInvariant(), StringComparison.Ordinal))
                    {
                        hits.Add(k);
                    }
                }

                if (hits.Count > 0)
                    matched.Add((rule, hits.Distinct(StringComparer.OrdinalIgnoreCase).ToList()));
            }

            if (matched.Count == 0)
            {
                return new NodeTaskModeResolution
                {
                    Mode = NodeTaskMode.Chat,
                    Reason = "未命中特定任務規則，使用預設 Chat",
                    Confidence = 0.55,
                    MatchedKeywords = Array.Empty<string>()
                };
            }

            var best = matched
                .OrderByDescending(x => x.Hits.Count)
                .ThenByDescending(x => x.Rule.Confidence)
                .First();

            return new NodeTaskModeResolution
            {
                Mode = best.Rule.Mode,
                Reason = best.Rule.Reason,
                Confidence = best.Rule.Confidence,
                MatchedKeywords = best.Hits
            };
        }
    }
}