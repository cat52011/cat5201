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
            string agentId,
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
            agentId ??= "";

            if (string.IsNullOrWhiteSpace(bottomText))
                return Task.CompletedTask;

            string fileKey = GetCurrentFileKey();
            string title = BuildTitle(topText, taskMode);

            var items = new List<MemoryItem>();

            // 1. Agent 專屬 execution memory
            items.Add(new MemoryItem
            {
                Scope = MemoryScope.Node,
                Category = "execution_result",
                FileKey = fileKey,
                SourceNodeId = node.Id.ToString(),
                AgentId = agentId,
                IsSharedMemory = false,
                Title = title,
                Content = TrimText(bottomText, 1800),
                Tags = BuildTags(topText, taskMode),
                TaskMode = NodeTaskModeHelper.ToStorageValue(taskMode),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                Importance = 0.68,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            // 2. Agent 專屬 file summary memory
            items.Add(new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "agent_summary",
                FileKey = fileKey,
                SourceNodeId = node.Id.ToString(),
                AgentId = agentId,
                IsSharedMemory = false,
                Title = $"Agent 摘要：{title}",
                Content = BuildFileLevelSummary(topText, bottomText),
                Tags = BuildTags(topText, taskMode),
                TaskMode = NodeTaskModeHelper.ToStorageValue(taskMode),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                Importance = 0.74,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            // 3. Shared memory
            items.Add(new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "shared_summary",
                FileKey = fileKey,
                SourceNodeId = node.Id.ToString(),
                AgentId = "",
                IsSharedMemory = true,
                Title = $"共享摘要：{title}",
                Content = BuildFileLevelSummary(topText, bottomText),
                Tags = BuildTags(topText, taskMode),
                TaskMode = NodeTaskModeHelper.ToStorageValue(taskMode),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                Importance = 0.70,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            _store.AddRange(items);
            return Task.CompletedTask;
        }

        public Task RememberCapabilityTraceAsync(
            NodeControl node,
            string agentId,
            string topText,
            IReadOnlyList<AgentCapabilityTraceItem> capabilityTrace,
            NodeTaskMode taskMode,
            string modelId,
            CancellationToken ct = default)
        {
            if (node == null || capabilityTrace == null || capabilityTrace.Count == 0)
                return Task.CompletedTask;

            agentId ??= "";
            topText ??= "";

            string fileKey = GetCurrentFileKey();
            string title = BuildTitle(topText, taskMode);

            var executed = capabilityTrace
                .Where(x => x != null && x.Executed)
                .ToList();

            if (executed.Count == 0)
                return Task.CompletedTask;

            string content = string.Join(
                "\n",
                executed.Select(x =>
                    $"- Capability={x.CapabilityId}, Handled={x.Handled}, Augmented={x.AugmentedPrompt}, Success={x.Success}, Summary={TrimText(x.Summary, 120)}"));

            var item = new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "capability_result",
                FileKey = fileKey,
                SourceNodeId = node.Id.ToString(),
                AgentId = agentId,
                IsSharedMemory = false,
                Title = $"Capability 摘要：{title}",
                Content = TrimText(content, 1200),
                Tags = BuildTags(topText, taskMode),
                TaskMode = NodeTaskModeHelper.ToStorageValue(taskMode),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                Importance = 0.62,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _store.Add(item);
            return Task.CompletedTask;
        }

        public Task RememberDelegationTraceAsync(
            NodeControl node,
            string agentId,
            string topText,
            IReadOnlyList<AgentDelegationTraceItem> delegationTrace,
            NodeTaskMode taskMode,
            string modelId,
            CancellationToken ct = default)
        {
            if (node == null || delegationTrace == null || delegationTrace.Count == 0)
                return Task.CompletedTask;

            agentId ??= "";
            topText ??= "";

            string fileKey = GetCurrentFileKey();
            string title = BuildTitle(topText, taskMode);

            var succeeded = delegationTrace
                .Where(x => x != null && x.Success)
                .ToList();

            if (succeeded.Count == 0)
                return Task.CompletedTask;

            string content = string.Join(
                "\n",
                succeeded.Select(x =>
                    $"- {x.FromAgentId} -> {x.ToAgentId}, Depth={x.Depth}, Summary={TrimText(x.OutputSummary, 180)}"));

            var item = new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "delegation_result",
                FileKey = fileKey,
                SourceNodeId = node.Id.ToString(),
                AgentId = agentId,
                IsSharedMemory = false,
                Title = $"Delegation 摘要：{title}",
                Content = TrimText(content, 1400),
                Tags = BuildTags(topText, taskMode),
                TaskMode = NodeTaskModeHelper.ToStorageValue(taskMode),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                Importance = 0.66,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _store.Add(item);
            return Task.CompletedTask;
        }
        public MemoryQueryResult RecallRelevant(
            NodeControl currentNode,
            string agentId,
            string topText,
            NodeTaskMode taskMode,
            int maxCount = 6)
        {
            string fileKey = GetCurrentFileKey();

            var all = _store.Query(fileKey, agentId, topText, maxCount * 2);

            var agentItems = all
                .Where(x => string.Equals(x.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                .Take(maxCount)
                .ToList();

            var sharedItems = all
                .Where(x => x.IsSharedMemory)
                .Take(3)
                .ToList();

            var merged = agentItems
    .Concat(sharedItems)
    .GroupBy(x => x.Id)
    .Select(g => g.First())
    .Take(maxCount)
    .ToList();

            string block = BuildPromptBlock(agentId, merged, agentItems, sharedItems, taskMode);

            return new MemoryQueryResult
            {
                Items = merged,
                AgentItems = agentItems,
                SharedItems = sharedItems,
                PromptBlock = block
            };
        }
        private string BuildPromptBlock(
            string agentId,
            IReadOnlyList<MemoryItem> items,
            IReadOnlyList<MemoryItem> agentItems,
            IReadOnlyList<MemoryItem> sharedItems,
            NodeTaskMode taskMode)
        {
            if ((items == null || items.Count == 0) &&
                (agentItems == null || agentItems.Count == 0) &&
                (sharedItems == null || sharedItems.Count == 0))
            {
                return "";
            }

            var lines = new List<string>
    {
        "【相關記憶（Agent-aware）】"
    };

            if (agentItems != null && agentItems.Count > 0)
            {
                lines.Add($"【Agent 專屬記憶：{agentId}】");
                int index = 1;
                foreach (var item in agentItems)
                {
                    lines.Add($"- Agent Memory {index}");
                    lines.Add($"- Agent Memory {index}");
                    lines.Add($"  Category: {item.Category}");
                    lines.Add($"  Title: {item.Title}");

                    if (string.Equals(item.Category, "capability_result", StringComparison.OrdinalIgnoreCase))
                        lines.Add("  Type: capability trace memory");
                    else if (string.Equals(item.Category, "delegation_result", StringComparison.OrdinalIgnoreCase))
                        lines.Add("  Type: delegation trace memory");
                    else if (string.Equals(item.Category, "execution_result", StringComparison.OrdinalIgnoreCase))
                        lines.Add("  Type: execution result memory");

                    lines.Add($"  Content: {TrimText(item.Content, 320)}");
                    index++;
                }
            }

            if (sharedItems != null && sharedItems.Count > 0)
            {
                lines.Add("【共享記憶】");
                int index = 1;
                foreach (var item in sharedItems)
                {
                    lines.Add($"- Shared Memory {index}");
                    lines.Add($"- Shared Memory {index}");
                    lines.Add($"  Category: {item.Category}");
                    lines.Add($"  Title: {item.Title}");

                    if (string.Equals(item.Category, "capability_result", StringComparison.OrdinalIgnoreCase))
                        lines.Add("  Type: capability trace memory");
                    else if (string.Equals(item.Category, "delegation_result", StringComparison.OrdinalIgnoreCase))
                        lines.Add("  Type: delegation trace memory");
                    else if (string.Equals(item.Category, "execution_result", StringComparison.OrdinalIgnoreCase))
                        lines.Add("  Type: execution result memory");

                    lines.Add($"  Content: {TrimText(item.Content, 280)}");
                    index++;
                }
            }

            lines.Add("要求：若記憶與目前節點衝突，以目前節點內容為準。");
            lines.Add("要求：Agent 專屬記憶優先於共享記憶。");

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