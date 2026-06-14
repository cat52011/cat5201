using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    /// <summary>
    /// 每個節點的工作流執行歷史 + 最近一次成功的 workspace 快取（§3）。
    /// 歷史供「可讀執行 log / 重播 / 從失敗續跑」使用；
    /// 快取的 workspace 供「重新生成答案」(沿用既有 capability 成果，跳過重跑 research) 使用。
    /// 全部存在記憶體；單一節點只保留最近 MaxHistoryPerNode 筆與最後一份成功 workspace。
    /// </summary>
    public sealed class WorkflowRunStore
    {
        private const int MaxHistoryPerNode = 20;

        private readonly object _sync = new();
        private readonly Dictionary<Guid, List<WorkflowRunRecord>> _history = new();
        private readonly Dictionary<Guid, CachedWorkspace> _cache = new();

        public void Record(Guid nodeId, WorkflowRunRecord record)
        {
            if (record == null)
                return;

            lock (_sync)
            {
                if (!_history.TryGetValue(nodeId, out var list))
                {
                    list = new List<WorkflowRunRecord>();
                    _history[nodeId] = list;
                }

                list.Add(record);

                if (list.Count > MaxHistoryPerNode)
                    list.RemoveRange(0, list.Count - MaxHistoryPerNode);
            }
        }

        public IReadOnlyList<WorkflowRunRecord> GetRuns(Guid nodeId)
        {
            lock (_sync)
            {
                return _history.TryGetValue(nodeId, out var list)
                    ? list.ToList()
                    : (IReadOnlyList<WorkflowRunRecord>)Array.Empty<WorkflowRunRecord>();
            }
        }

        public WorkflowRunRecord? GetLast(Guid nodeId)
        {
            lock (_sync)
            {
                return _history.TryGetValue(nodeId, out var list) && list.Count > 0
                    ? list[list.Count - 1]
                    : null;
            }
        }

        /// <summary>快取一次成功執行的 workspace + 完整輸入，供「重新生成答案」沿用。</summary>
        public void CacheWorkspace(Guid nodeId, AgentWorkspace workspace, string originalInput)
        {
            if (workspace == null)
                return;

            lock (_sync)
            {
                _cache[nodeId] = new CachedWorkspace(workspace, originalInput ?? "");
            }
        }

        public bool TryGetCachedWorkspace(Guid nodeId, out AgentWorkspace? workspace, out string originalInput)
        {
            lock (_sync)
            {
                if (_cache.TryGetValue(nodeId, out var cached))
                {
                    workspace = cached.Workspace;
                    originalInput = cached.OriginalInput;
                    return true;
                }
            }

            workspace = null;
            originalInput = "";
            return false;
        }

        public bool HasCachedWorkspace(Guid nodeId)
        {
            lock (_sync)
            {
                return _cache.ContainsKey(nodeId);
            }
        }

        public void Clear(Guid nodeId)
        {
            lock (_sync)
            {
                _history.Remove(nodeId);
                _cache.Remove(nodeId);
            }
        }

        private sealed class CachedWorkspace
        {
            public CachedWorkspace(AgentWorkspace workspace, string originalInput)
            {
                Workspace = workspace;
                OriginalInput = originalInput;
            }

            public AgentWorkspace Workspace { get; }
            public string OriginalInput { get; }
        }
    }
}
