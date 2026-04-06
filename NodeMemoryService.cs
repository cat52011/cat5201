using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class NodeMemoryService
    {
        private readonly MainWindow _main;
        private readonly MemoryStore _store;

        public NodeMemoryService(MainWindow main, MemoryStore store)
        {
            _main = main;
            _store = store;
        }

        public Task RememberExecutionResultAsync(
            NodeControl node,
            string topText,
            string bottomText,
            NodeTaskMode taskMode,
            string modelId,
            CancellationToken ct = default)
        {
            if (node == null)
                return Task.CompletedTask;

            topText ??= "";
            bottomText ??= "";

            if (string.IsNullOrWhiteSpace(bottomText))
                return Task.CompletedTask;

            string fileKey = GetCurrentFileKey();
            string title = BuildTitle(topText, taskMode);

            var items = new List<MemoryItem>();

            // 1. 節點結果記憶
            items.Add(new MemoryItem
            {
                Scope = MemoryScope.Node,
                Category = "execution_result",
                FileKey = fileKey,
                SourceNodeId = node.Id.ToString(),
                Title = title,
                Content = TrimText(bottomText, 1800),
                Tags = BuildTags(topText, taskMode),
                TaskMode = NodeTaskModeHelper.ToStorageValue(taskMode),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                Importance = 0.60,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            // 2. 檔案級摘要記憶
            items.Add(new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "summary",
                FileKey = fileKey,
                SourceNodeId = node.Id.ToString(),
                Title = $"檔案脈絡摘要：{title}",
                Content = BuildFileLevelSummary(topText, bottomText),
                Tags = BuildTags(topText, taskMode),
                TaskMode = NodeTaskModeHelper.ToStorageValue(taskMode),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                Importance = 0.72,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            _store.AddRange(items);
            return Task.CompletedTask;
        }

        public MemoryQueryResult RecallRelevant(
            NodeControl currentNode,
            string topText,
            NodeTaskMode taskMode,
            int maxCount = 6)
        {
            string fileKey = GetCurrentFileKey();
            var items = _store.Query(fileKey, topText, maxCount);

            string block = BuildPromptBlock(items, taskMode);
            return new MemoryQueryResult
            {
                Items = items,
                PromptBlock = block
            };
        }

        private string BuildPromptBlock(
            IReadOnlyList<MemoryItem> items,
            NodeTaskMode taskMode)
        {
            if (items == null || items.Count == 0)
                return "";

            var lines = new List<string>
            {
                "【相關記憶（中高權重，僅供延續脈絡，不可蓋過目前節點）】"
            };

            int index = 1;
            foreach (var item in items)
            {
                lines.Add($"- 記憶 {index}");
                lines.Add($"  Scope: {item.Scope}");
                lines.Add($"  Category: {item.Category}");
                lines.Add($"  Title: {item.Title}");
                lines.Add($"  Content: {TrimText(item.Content, 320)}");
                index++;
            }

            lines.Add("要求：若相關記憶與目前節點衝突，以目前節點內容為準。");

            return string.Join(Environment.NewLine, lines);
        }

        private string BuildFileLevelSummary(string topText, string bottomText)
        {
            return
                $"使用者要求：{TrimText(topText, 240)}\n" +
                $"本次結果摘要：{TrimText(bottomText, 420)}";
        }

        private string[] BuildTags(string topText, NodeTaskMode taskMode)
        {
            var tags = new List<string>
            {
                NodeTaskModeHelper.ToDisplayName(taskMode)
            };

            foreach (var token in SplitKeywords(topText).Take(8))
                tags.Add(token);

            return tags
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        private string BuildTitle(string topText, NodeTaskMode taskMode)
        {
            string prefix = NodeTaskModeHelper.ToDisplayName(taskMode);
            string shortText = TrimText(topText?.Trim() ?? "", 36);

            if (string.IsNullOrWhiteSpace(shortText))
                return prefix;

            return $"{prefix} - {shortText}";
        }

        private string GetCurrentFileKey()
        {
            // 第一版先用目前主視窗顯示檔名當 key
            // 後續若你想更穩，可以改成 MainWindow 直接公開 CurrentFilePath / FileId
            try
            {
                var label = _main.CurrentFileDisplayKey();
                return string.IsNullOrWhiteSpace(label) ? "default" : label;
            }
            catch
            {
                return "default";
            }
        }

        private static IEnumerable<string> SplitKeywords(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                yield break;

            var separators = new[]
            {
                ' ', '\r', '\n', '\t',
                ',', '，', '。', '.', ':', '：', ';', '；',
                '(', ')', '（', '）', '[', ']', '{', '}',
                '/', '\\', '|', '-', '_', '、'
            };

            foreach (var part in text.Split(separators, StringSplitOptions.RemoveEmptyEntries))
            {
                var token = part.Trim();
                if (token.Length >= 2)
                    yield return token;
            }
        }

        private static string TrimText(string text, int max)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            text = text.Trim();
            if (text.Length <= max)
                return text;

            return text.Substring(0, max) + "…";
        }
    }
}