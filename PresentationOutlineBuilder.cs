using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace test
{
    /// <summary>
    /// Presentation Agent v1：把 final answer 確定性地拆解成投影片大綱，並渲染成 Marp 相容的 Markdown deck。
    /// 純字串處理、無外部依賴、不額外呼叫 LLM（重用最終合成的內容以節省成本）。
    /// 解析規則：短的非條列行視為段落標題（一張投影片），條列行 / 長段落視為該投影片的重點。
    /// </summary>
    public static class PresentationOutlineBuilder
    {
        // 每張投影片最多重點數，超過則拆成「（續）」投影片，避免單頁爆滿。
        private const int MaxBulletsPerSlide = 6;

        // 被視為「段落標題」的非條列行最大長度。
        private const int MaxHeadingLength = 24;

        public sealed class Request
        {
            public string UserInput { get; init; } = "";
            public string FinalAnswer { get; init; } = "";
            public AgentWorkspace? Workspace { get; init; }
            public string PipelineId { get; init; } = "";
            public string ModelId { get; init; } = "";
            public string AgentId { get; init; } = "";
        }

        public static PresentationOutlinePayload Build(Request request)
        {
            request ??= new Request();

            string title = BuildTitle(request.UserInput);
            int requested = DetectRequestedSlideCount(request.UserInput);

            var slides = new List<PresentationSlidePayload>();
            int order = 1;

            // 封面
            slides.Add(new PresentationSlidePayload
            {
                Order = order++,
                Kind = "cover",
                Heading = title,
                Bullets = Array.Empty<string>()
            });

            // 內容投影片
            foreach (var section in ParseSections(request.FinalAnswer))
            {
                foreach (var chunk in ChunkBullets(section.Bullets, MaxBulletsPerSlide))
                {
                    bool isContinuation = chunk.IsContinuation;
                    slides.Add(new PresentationSlidePayload
                    {
                        Order = order++,
                        Kind = "content",
                        Heading = isContinuation ? section.Heading + "（續）" : section.Heading,
                        Bullets = chunk.Bullets
                    });
                }
            }

            // 資料來源投影片（若有 verified_facts 帶來源）
            var sourceBullets = BuildSourceBullets(request.Workspace);
            if (sourceBullets.Count > 0)
            {
                slides.Add(new PresentationSlidePayload
                {
                    Order = order++,
                    Kind = "sources",
                    Heading = "資料來源",
                    Bullets = sourceBullets
                });
            }

            return new PresentationOutlinePayload
            {
                Title = title,
                Topic = OneLine(request.UserInput),
                Slides = slides,
                SlideCount = slides.Count,
                RequestedSlideCount = requested,
                PipelineId = request.PipelineId,
                ModelId = request.ModelId,
                AgentId = request.AgentId,
                SourceSummary = ""
            };
        }

        /// <summary>
        /// 把大綱渲染成 Marp 相容的 Markdown deck：YAML front matter + 以 --- 分隔的投影片。
        /// 同時也是可直接閱讀的純 Markdown。
        /// </summary>
        public static string RenderMarkdownDeck(PresentationOutlinePayload outline)
            => RenderMarkdownDeck(outline, null);

        // coverImageFileName：非 null 時，在封面投影片嵌入圖片（與 .md 同資料夾，用相對檔名引用）。
        public static string RenderMarkdownDeck(PresentationOutlinePayload outline, string? coverImageFileName)
        {
            outline ??= new PresentationOutlinePayload();

            var sb = new StringBuilder();

            // Marp front matter（讓檔案可直接被 Marp 轉成投影片；對純文字閱讀也無害）。
            sb.AppendLine("---");
            sb.AppendLine("marp: true");
            sb.AppendLine("paginate: true");
            sb.AppendLine("---");
            sb.AppendLine();

            var metadata = BuildMetadataLine(outline);

            bool first = true;
            foreach (var slide in outline.Slides ?? Array.Empty<PresentationSlidePayload>())
            {
                if (!first)
                {
                    sb.AppendLine();
                    sb.AppendLine("---");
                    sb.AppendLine();
                }
                first = false;

                if (string.Equals(slide.Kind, "cover", StringComparison.Ordinal))
                {
                    sb.AppendLine($"# {slide.Heading}");
                    if (!string.IsNullOrWhiteSpace(outline.Topic))
                    {
                        sb.AppendLine();
                        sb.AppendLine(outline.Topic);
                    }
                    if (!string.IsNullOrWhiteSpace(coverImageFileName))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"![{slide.Heading}]({coverImageFileName})");
                    }
                    if (!string.IsNullOrWhiteSpace(metadata))
                    {
                        sb.AppendLine();
                        sb.AppendLine($"> {metadata}");
                    }
                }
                else
                {
                    sb.AppendLine($"## {slide.Heading}");
                    if (slide.Bullets != null && slide.Bullets.Count > 0)
                    {
                        sb.AppendLine();
                        foreach (var bullet in slide.Bullets)
                            sb.AppendLine($"- {bullet}");
                    }
                }
            }

            return sb.ToString().TrimEnd() + Environment.NewLine;
        }

        private static string BuildMetadataLine(PresentationOutlinePayload outline)
        {
            var parts = new List<string>
            {
                $"產生時間：{DateTime.Now:yyyy-MM-dd HH:mm}"
            };

            if (!string.IsNullOrWhiteSpace(outline.AgentId))
                parts.Add($"代理：{outline.AgentId}");
            if (!string.IsNullOrWhiteSpace(outline.ModelId))
                parts.Add($"模型：{outline.ModelId}");
            if (!string.IsNullOrWhiteSpace(outline.PipelineId))
                parts.Add($"管線：{outline.PipelineId}");

            return string.Join(" ｜ ", parts);
        }

        private sealed class Section
        {
            public string Heading = "";
            public List<string> Bullets = new List<string>();
        }

        private static IReadOnlyList<Section> ParseSections(string finalAnswer)
        {
            var sections = new List<Section>();
            Section? current = null;

            string text = (finalAnswer ?? "").Replace("\r\n", "\n").Replace("\r", "\n");

            void EnsureSection(string defaultHeading)
            {
                if (current == null)
                {
                    current = new Section { Heading = defaultHeading };
                    sections.Add(current);
                }
            }

            foreach (var raw in text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.Length == 0)
                    continue;

                bool hadHash = line.StartsWith("#", StringComparison.Ordinal);
                string stripped = StripMarkers(line);
                if (stripped.Length == 0)
                    continue;

                string? bulletText = TryExtractBullet(line);

                if (bulletText != null)
                {
                    EnsureSection("重點");
                    current!.Bullets.Add(bulletText);
                    continue;
                }

                bool looksLikeHeading =
                    hadHash ||
                    (stripped.Length <= MaxHeadingLength && !EndsLikeSentence(stripped));

                if (looksLikeHeading)
                {
                    current = new Section { Heading = stripped };
                    sections.Add(current);
                }
                else
                {
                    // 一般段落：當成目前段落的一條重點。
                    EnsureSection("說明");
                    current!.Bullets.Add(stripped);
                }
            }

            // 移除完全沒有重點的空段落（例如只有標題、沒有內容）。
            return sections.Where(s => s.Bullets.Count > 0).ToList();
        }

        private sealed class BulletChunk
        {
            public bool IsContinuation;
            public IReadOnlyList<string> Bullets = Array.Empty<string>();
        }

        private static IEnumerable<BulletChunk> ChunkBullets(List<string> bullets, int maxPerSlide)
        {
            if (bullets == null || bullets.Count == 0)
            {
                yield return new BulletChunk { IsContinuation = false, Bullets = Array.Empty<string>() };
                yield break;
            }

            bool firstChunk = true;
            for (int i = 0; i < bullets.Count; i += maxPerSlide)
            {
                yield return new BulletChunk
                {
                    IsContinuation = !firstChunk,
                    Bullets = bullets.Skip(i).Take(maxPerSlide).ToList()
                };
                firstChunk = false;
            }
        }

        private static IReadOnlyList<string> BuildSourceBullets(AgentWorkspace? workspace)
        {
            if (workspace == null)
                return Array.Empty<string>();

            var facts = workspace.GetByType("verified_facts")
                .Select(x => x.Payload)
                .OfType<VerifiedFactPayload>()
                .SelectMany(p => p.Facts ?? Array.Empty<VerifiedFactItem>())
                .Where(f => f != null &&
                    (!string.IsNullOrWhiteSpace(f.SourceTitle) || !string.IsNullOrWhiteSpace(f.SourceUrl)))
                .GroupBy(f => (f.SourceTitle ?? "").Trim() + "|" + (f.SourceUrl ?? "").Trim())
                .Select(g => g.First())
                .Take(8)
                .ToList();

            var bullets = new List<string>();
            foreach (var f in facts)
            {
                string label = string.IsNullOrWhiteSpace(f.SourceTitle)
                    ? f.SourceUrl.Trim()
                    : f.SourceTitle.Trim();

                bullets.Add(string.IsNullOrWhiteSpace(f.SourceUrl)
                    ? label
                    : $"{label} ({f.SourceUrl.Trim()})");
            }

            return bullets;
        }

        // ---- 解析輔助 ----

        // 條列符號：- * • ・ 以及「1. / 1、 / 1)」等編號清單。
        private static readonly Regex NumberedBullet =
            new Regex(@"^\d+\s*[\.、\)]\s+", RegexOptions.Compiled);

        private static string? TryExtractBullet(string line)
        {
            if (line.Length == 0)
                return null;

            char c = line[0];
            if (c == '-' || c == '*' || c == '•' || c == '・')
            {
                string rest = line.Substring(1).TrimStart();
                return rest.Length > 0 ? rest : null;
            }

            var m = NumberedBullet.Match(line);
            if (m.Success)
            {
                string rest = line.Substring(m.Length).Trim();
                return rest.Length > 0 ? rest : null;
            }

            return null;
        }

        private static string StripMarkers(string line)
        {
            string s = line.TrimStart('#', ' ', '\t');
            // 去除粗體 / 標題殘留的星號與冒號尾巴。
            s = s.Trim().Trim('*').Trim();
            return s;
        }

        private static bool EndsLikeSentence(string s)
        {
            if (string.IsNullOrEmpty(s))
                return false;

            char last = s[s.Length - 1];
            // 句末標點（中英）或冒號代表這是內容句，不是段落標題。
            return "。．.！!？?；;，,、：:".IndexOf(last) >= 0;
        }

        private static readonly Regex SlideCountPattern =
            new Regex(@"(\d+)\s*(頁|張|頁簡報|張投影片|slides?|slide)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        private static readonly Dictionary<string, int> CjkNumbers = new Dictionary<string, int>
        {
            ["三"] = 3, ["五"] = 5, ["十"] = 10, ["七"] = 7, ["八"] = 8, ["六"] = 6, ["四"] = 4, ["九"] = 9
        };

        public static int DetectRequestedSlideCount(string userInput)
        {
            string text = userInput ?? "";

            var m = SlideCountPattern.Match(text);
            if (m.Success && int.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n))
            {
                if (n >= 1 && n <= 50)
                    return n;
            }

            // 中文數字 + 頁/張
            foreach (var kv in CjkNumbers)
            {
                if (text.Contains(kv.Key + "頁", StringComparison.Ordinal) ||
                    text.Contains(kv.Key + "張", StringComparison.Ordinal))
                {
                    return kv.Value;
                }
            }

            return 0;
        }

        private static string BuildTitle(string userInput)
        {
            string text = OneLine(userInput);
            if (string.IsNullOrWhiteSpace(text))
                return "簡報";

            // 移除常見的祈使動詞，讓標題更像主題而非指令。
            foreach (var prefix in new[] { "幫我", "請", "麻煩", "幫忙" })
            {
                if (text.StartsWith(prefix, StringComparison.Ordinal))
                    text = text.Substring(prefix.Length).TrimStart();
            }

            if (text.Length > 40)
                text = text.Substring(0, 40).TrimEnd() + "…";

            return text;
        }

        private static string OneLine(string text)
        {
            return (text ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
        }
    }
}
