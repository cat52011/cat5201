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
        private readonly PreferenceExtractor _prefExtractor = new();

        public NodeMemoryService(MainWindow main, MemoryStore store)
        {
            _main = main;
            _store = store;
        }

        // ===== Memory v1：偏好記憶 =====

        /// <summary>
        /// 手動輸入框：把整段文字當明確偏好擷取並 upsert。回傳擷取到的偏好顯示值（給 UI 顯示）。
        /// </summary>
        public IReadOnlyList<string> CaptureExplicitPreference(string text)
        {
            var detected = _prefExtractor.ExtractFromManualInput(text);
            return UpsertDetected(detected);
        }

        // 註：全域記憶（偏好）改為「只能手動輸入或右鍵節點加入記憶」，不再從任務文字自動被動擷取。
        // 原 CapturePassivePreference 已停用移除；PreferenceExtractor.ExtractFromTaskText 暫保留備用，目前無呼叫者。

        private IReadOnlyList<string> UpsertDetected(IReadOnlyList<PreferenceExtractor.DetectedPreference> detected)
        {
            var captured = new List<string>();
            if (detected == null) return captured;

            foreach (var p in detected)
            {
                _store.UpsertPreference(new MemoryItem
                {
                    Scope = MemoryScope.Global,
                    Category = "user_preference",
                    PreferenceKey = p.Key,
                    IsSharedMemory = true,
                    Title = p.DisplayValue,
                    Content = p.Content,
                    Importance = p.Importance,
                    CreatedAtUtc = DateTime.UtcNow,
                    UpdatedAtUtc = DateTime.UtcNow
                });
                captured.Add($"{p.DisplayValue}");
            }

            return captured;
        }

        public int ClearAllMemory() => _store.ClearAll();
        public int ClearPreferences() => _store.ClearPreferences();
        public int ClearEpisodicMemory() => _store.ClearEpisodic();

        /// <summary>清除「當前記憶」清單顯示的全部內容＝偏好 + 使用者標記（user_marked）。</summary>
        public int ClearShownMemory() => _store.ClearPreferences() + _store.ClearUserMarked();
        public (int preferences, int episodic) GetMemoryStats() => _store.GetStats();

        public IReadOnlyList<string> GetPreferenceDisplayList() =>
            _store.GetPreferences()
                .Select(x => string.IsNullOrWhiteSpace(x.Title) ? x.Content : x.Title)
                .ToList();

        // user_marked 在「當前記憶」清單的刪除 key 前綴；DeletePreference 依此前綴改走 by-Id 刪除。
        public const string MarkedKeyPrefix = "__marked__:";

        /// <summary>個人化「當前記憶」清單：偏好 + 使用者明確標記的節點記憶；整句顯示 + 可刪除的 key。</summary>
        public IReadOnlyList<PreferenceView> GetPreferenceItems()
        {
            var list = _store.GetPreferences()
                .Select(x => new PreferenceView
                {
                    Display = string.IsNullOrWhiteSpace(x.Title) ? x.Content : x.Title,
                    Key = x.PreferenceKey
                })
                .ToList();

            // 使用者右鍵「加入記憶」的節點（user_marked）也要出現在當前記憶中，並可個別刪除。
            foreach (var m in _store.GetUserMarked())
            {
                list.Add(new PreferenceView
                {
                    Display = string.IsNullOrWhiteSpace(m.Title) ? m.Content : m.Title,
                    Key = MarkedKeyPrefix + m.Id
                });
            }

            return list;
        }

        public int DeletePreference(string key)
        {
            if (!string.IsNullOrEmpty(key) && key.StartsWith(MarkedKeyPrefix, StringComparison.Ordinal))
                return _store.RemoveById(key.Substring(MarkedKeyPrefix.Length));

            return _store.RemovePreference(key);
        }

        /// <summary>取得偏好區塊文字（給 AgentRuntime final synthesis 用，不需要 node）。</summary>
        public string GetPreferenceBlock() => BuildPreferenceBlock(_store.GetPreferences());

        /// <summary>
        /// 使用者右鍵「將此節點加入記憶」：把節點內容存成一筆最高重要性的共享記憶。
        /// SourceNodeId 記錄來源節點僅供「同節點重標時去重」；user_marked 在 RecallRelevant 一律視為全域、
        /// 不受跨鏈隔離（見該方法的 category 例外），所以仍是全畫布可召回。
        /// 同一節點重複標記 → 先移除舊的再寫新的（避免清單出現重複）。
        /// 回傳標題供 UI 提示。
        /// </summary>
        public string RememberNodeManually(
            NodeControl node,
            string agentId,
            string topText,
            string bottomText,
            NodeTaskMode taskMode,
            string modelId)
        {
            topText ??= "";
            bottomText ??= "";

            string fileKey = GetCurrentFileKey();
            string title = BuildTitle(topText, taskMode);
            string sourceNodeId = node?.Id.ToString() ?? "";

            // 同一節點先前已標記過 → 去重（覆蓋更新）。
            if (!string.IsNullOrEmpty(sourceNodeId))
                _store.RemoveUserMarkedByNode(sourceNodeId);

            _store.Add(new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "user_marked",
                FileKey = fileKey,
                SourceNodeId = sourceNodeId,  // 僅供同節點去重；recall 對 user_marked 一律全域
                AgentId = agentId ?? "",
                IsSharedMemory = true,
                Title = $"⭐ 使用者標記：{title}",
                Content = BuildFileLevelSummary(topText, bottomText),
                Tags = BuildTags(topText, taskMode),
                TaskMode = NodeTaskModeHelper.ToStorageValue(taskMode),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                Importance = 0.97,            // 高於自動記憶，召回排序優先
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            });

            return title;
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

            // 全域偏好不再自動被動擷取（改為只接受手動輸入 / 右鍵加入記憶）。
            // 這裡仍照常寫入 episodic（執行/Agent/共享）記憶供同鏈召回。

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

            IEnumerable<MemoryItem> all = _store.Query(fileKey, agentId, topText, maxCount * 2);

            // 跨鏈隔離：episodic / 執行類記憶只保留「同一條有向鏈」（本節點 + 祖先 + 後代）的節點所產生的，
            // 避免畫布上其它分支（兄弟 / 旁系，例如另一個掛附件的節點）的內容污染本節點——
            // 使用者要求：不在同一條鏈上的節點只要「大概知道」（靠 NodeContextService 支線摘要），不可被詳細注入。
            // 偏好記憶走獨立路徑（GetPreferences），不受此限、維持全域。
            // 例外：使用者明確要求「統整整個畫布」時，不隔離。
            if (currentNode != null && !WantsWholeCanvasSummary(topText))
            {
                var chainIds = _main.GetSameChainNodeIds(currentNode);
                all = all.Where(x =>
                    string.IsNullOrEmpty(x.SourceNodeId)
                    || string.Equals(x.Category, "user_marked", StringComparison.OrdinalIgnoreCase) // 使用者明確標記：一律全域，不受跨鏈隔離
                    || chainIds.Contains(x.SourceNodeId));
            }

            var allList = all.ToList();

            var agentItems = allList
                .Where(x => string.Equals(x.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                .Take(maxCount)
                .ToList();

            var sharedItems = allList
                .Where(x => x.IsSharedMemory)
                .Take(3)
                .ToList();

            var merged = agentItems
    .Concat(sharedItems)
    .GroupBy(x => x.Id)
    .Select(g => g.First())
    .Take(maxCount)
    .ToList();

            // 使用者偏好一律注入（不受相關度過濾），與 episodic 分開回傳。
            var preferences = _store.GetPreferences();

            string preferenceBlock = BuildPreferenceBlock(preferences);
            string block = BuildPromptBlock(agentId, merged, agentItems, sharedItems, taskMode);

            return new MemoryQueryResult
            {
                Items = merged,
                AgentItems = agentItems,
                SharedItems = sharedItems,
                PromptBlock = block,
                PreferenceBlock = preferenceBlock
            };
        }

        // 偵測使用者是否明確要求「統整 / 彙整整個畫布」——此時記憶召回不做跨鏈隔離，允許看到全畫布內容。
        private static bool WantsWholeCanvasSummary(string topText)
        {
            string s = (topText ?? "").ToLowerInvariant();
            if (s.Length == 0) return false;

            string[] keys =
            {
                "整個畫布", "整張畫布", "全畫布", "全部節點", "所有節點",
                "整個流程", "統整畫布", "彙整畫布", "彙總畫布", "綜整畫布",
                "whole canvas", "entire canvas", "all nodes", "across the canvas"
            };

            foreach (var k in keys)
                if (s.Contains(k, StringComparison.Ordinal))
                    return true;

            return false;
        }

        // Memory v1 視覺化：回報本次召回的偏好 / 記憶計數與簡短說明，供 decision-viz 顯示。
        // 與實際執行（NodeExecutionCoreService）相同的略過規則，避免顯示與真正注入不一致。
        public MemoryRecallStats GetRecallStats(
            NodeControl currentNode,
            string agentId,
            string topText,
            NodeTaskMode taskMode)
        {
            bool suppressed =
                taskMode != NodeTaskMode.Translate &&
                taskMode != NodeTaskMode.Rewrite &&
                taskMode != NodeTaskMode.Summarize &&
                FinanceTaskDetector.IsFinanceLike(topText);

            if (suppressed)
            {
                return new MemoryRecallStats
                {
                    Suppressed = true,
                    SuppressReason = "財經即時任務：本次略過記憶注入，僅用最新查證事實。"
                };
            }

            var result = RecallRelevant(currentNode, agentId, topText, taskMode);
            var preferences = _store.GetPreferences();

            var details = new List<string>();

            foreach (var p in preferences.Take(6))
                details.Add($"偏好：{TrimText(p.Content, 60)}");

            foreach (var item in result.Items.Take(6))
            {
                string label = string.IsNullOrWhiteSpace(item.Title) ? item.Content : item.Title;
                details.Add($"記憶：{CategoryLabel(item.Category)} · {TrimText(label, 48)}");
            }

            return new MemoryRecallStats
            {
                PreferenceCount = preferences.Count,
                EpisodicCount = result.Items.Count,
                AgentCount = result.AgentItems.Count,
                SharedCount = result.SharedItems.Count,
                Details = details
            };
        }

        private static string CategoryLabel(string? category)
        {
            return (category ?? "").ToLowerInvariant() switch
            {
                "execution_result" => "執行結果",
                "user_marked" => "使用者標記",
                "capability_result" => "能力追蹤",
                "delegation_result" => "委派追蹤",
                "summary" => "摘要",
                "fact" => "查證事實",
                "source_note" => "來源註記",
                "extracted_data" => "擷取資料",
                _ => string.IsNullOrWhiteSpace(category) ? "記憶" : category
            };
        }

        // 偏好區塊：獨立於 episodic 記憶，由 prompt builder 放在最上方當硬指令。
        private string BuildPreferenceBlock(IReadOnlyList<MemoryItem> preferences)
        {
            if (preferences == null || preferences.Count == 0)
                return "";

            var lines = new List<string>
            {
                "【使用者全域記憶 / 偏好（高優先，需遵守）】",
                "衝突處理規則：",
                "1) 若本節點的輸入明顯與下列偏好牴觸，以「本節點輸入」為準（節點優先）。",
                "2) 若下列偏好彼此矛盾，以「較新（排在越前面）」的為準——清單已依加入時間由新到舊排列。"
            };

            // 已由 GetPreferences 依 UpdatedAtUtc 由新到舊排序：越上面＝越新＝衝突時優先。
            foreach (var p in preferences)
                lines.Add($"- {p.Content}");

            return string.Join(Environment.NewLine, lines);
        }

        private string BuildPromptBlock(
            string agentId,
            IReadOnlyList<MemoryItem> items,
            IReadOnlyList<MemoryItem> agentItems,
            IReadOnlyList<MemoryItem> sharedItems,
            NodeTaskMode taskMode)
        {
            bool hasEpisodic =
                (items != null && items.Count > 0) ||
                (agentItems != null && agentItems.Count > 0) ||
                (sharedItems != null && sharedItems.Count > 0);

            if (!hasEpisodic)
                return "";

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
        public Task RememberWorkspaceSummaryAsync(
        NodeControl node,
        string agentId,
        string topText,
        AgentWorkspaceSummary workspaceSummary,
        NodeTaskMode taskMode,
        string modelId,
        CancellationToken ct = default)
        {
            if (node == null || workspaceSummary == null)
                return Task.CompletedTask;

            if (string.IsNullOrWhiteSpace(workspaceSummary.SummaryText))
                return Task.CompletedTask;

            string fileKey = GetCurrentFileKey();
            string title = BuildTitle(topText, taskMode);

            var item = new MemoryItem
            {
                Scope = MemoryScope.File,
                Category = "workspace_summary",
                FileKey = fileKey,
                SourceNodeId = node.Id.ToString(),
                AgentId = agentId ?? "",
                IsSharedMemory = true,
                Title = $"Workspace 摘要：{title}",
                Content = TrimText(workspaceSummary.SummaryText, 1800),
                Tags = BuildTags(topText, taskMode)
                    .Concat(workspaceSummary.ItemTypes ?? Array.Empty<string>())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToArray(),
                TaskMode = NodeTaskModeHelper.ToStorageValue(taskMode),
                ModelId = AiModelHelper.NormalizeNodeModel(modelId),
                Importance = 0.72,
                CreatedAtUtc = DateTime.UtcNow,
                UpdatedAtUtc = DateTime.UtcNow
            };

            _store.Add(item);
            return Task.CompletedTask;
        }

    }

}