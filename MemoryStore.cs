using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace test
{
    public sealed class MemoryStore
    {
        private readonly string _memoryRootDir;
        private readonly string _memoryFilePath;

        private readonly object _sync = new();
        private List<MemoryItem> _cache = new();

        public MemoryStore(string savesDir)
        {
            _memoryRootDir = Path.Combine(savesDir, "_memory");
            _memoryFilePath = Path.Combine(_memoryRootDir, "memory_store.json");

            Directory.CreateDirectory(_memoryRootDir);
            Load();
        }

        public IReadOnlyList<MemoryItem> GetAll()
        {
            lock (_sync)
            {
                return _cache.ToList();
            }
        }

        public void Add(MemoryItem item)
        {
            if (item == null || string.IsNullOrWhiteSpace(item.Content))
                return;

            lock (_sync)
            {
                _cache.Add(item);
                TrimUnsafe();
                SaveUnsafe();
            }
        }

        public void AddRange(IEnumerable<MemoryItem> items)
        {
            if (items == null)
                return;

            lock (_sync)
            {
                foreach (var item in items)
                {
                    if (item == null || string.IsNullOrWhiteSpace(item.Content))
                        continue;

                    _cache.Add(item);
                }

                TrimUnsafe();
                SaveUnsafe();
            }
        }

        /// <summary>
        /// 偏好 upsert：同一個 PreferenceKey 只保留最新一筆（覆蓋舊偏好）。
        /// </summary>
        public void UpsertPreference(MemoryItem pref)
        {
            if (pref == null || string.IsNullOrWhiteSpace(pref.Content) ||
                string.IsNullOrWhiteSpace(pref.PreferenceKey))
                return;

            lock (_sync)
            {
                _cache.RemoveAll(x =>
                    string.Equals(x.Category, "user_preference", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.PreferenceKey, pref.PreferenceKey, StringComparison.OrdinalIgnoreCase));

                _cache.Add(pref);
                SaveUnsafe();
            }
        }

        /// <summary>
        /// 取得所有使用者偏好（Global / user_preference），依重要度排序。
        /// </summary>
        public IReadOnlyList<MemoryItem> GetPreferences()
        {
            lock (_sync)
            {
                return _cache
                    .Where(x => string.Equals(x.Category, "user_preference", StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(x => x.Importance)
                    .ThenByDescending(x => x.UpdatedAtUtc)
                    .ToList();
            }
        }

        /// <summary>依 PreferenceKey 刪除單一偏好。</summary>
        public int RemovePreference(string preferenceKey)
        {
            if (string.IsNullOrWhiteSpace(preferenceKey))
                return 0;

            lock (_sync)
            {
                int n = _cache.RemoveAll(x =>
                    string.Equals(x.Category, "user_preference", StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(x.PreferenceKey, preferenceKey, StringComparison.Ordinal));
                SaveUnsafe();
                return n;
            }
        }

        /// <summary>清除全部記憶（偏好 + episodic）。</summary>
        public int ClearAll()
        {
            lock (_sync)
            {
                int n = _cache.Count;
                _cache = new List<MemoryItem>();
                SaveUnsafe();
                return n;
            }
        }

        /// <summary>只清除使用者偏好，保留 episodic 執行記憶。</summary>
        public int ClearPreferences()
        {
            lock (_sync)
            {
                int n = _cache.RemoveAll(x =>
                    string.Equals(x.Category, "user_preference", StringComparison.OrdinalIgnoreCase));
                SaveUnsafe();
                return n;
            }
        }

        /// <summary>只清除 episodic 執行記憶，保留偏好。</summary>
        public int ClearEpisodic()
        {
            lock (_sync)
            {
                int n = _cache.RemoveAll(x =>
                    !string.Equals(x.Category, "user_preference", StringComparison.OrdinalIgnoreCase));
                SaveUnsafe();
                return n;
            }
        }

        /// <summary>(偏好筆數, episodic 筆數)。</summary>
        public (int preferences, int episodic) GetStats()
        {
            lock (_sync)
            {
                int pref = _cache.Count(x =>
                    string.Equals(x.Category, "user_preference", StringComparison.OrdinalIgnoreCase));
                return (pref, _cache.Count - pref);
            }
        }

        public IReadOnlyList<MemoryItem> Query(
    string fileKey,
    string agentId,
    string text,
    int maxCount = 6)
        {
            text ??= "";
            fileKey ??= "";
            agentId ??= "";

            string normalizedQuery = Normalize(text);

            lock (_sync)
            {
                var ranked = _cache
                    .Where(x => !string.Equals(x.Category, "user_preference", StringComparison.OrdinalIgnoreCase))
                    .Where(x => string.IsNullOrWhiteSpace(fileKey) ||
                                string.Equals(x.FileKey, fileKey, StringComparison.OrdinalIgnoreCase))
                    .Select(x => new
                    {
                        Item = x,
                        Score = Score(x, fileKey, agentId, normalizedQuery)
                    })
                    .Where(x => x.Score > 0)
                    .OrderByDescending(x => x.Score)
                    .ThenByDescending(x => x.Item.Importance)
                    .ThenByDescending(x => x.Item.UpdatedAtUtc)
                    .Take(maxCount)
                    .Select(x => x.Item)
                    .ToList();

                return ranked;
            }
        }

        private void Load()
        {
            lock (_sync)
            {
                try
                {
                    if (!File.Exists(_memoryFilePath))
                    {
                        _cache = new List<MemoryItem>();
                        return;
                    }

                    var json = File.ReadAllText(_memoryFilePath);
                    var list = JsonSerializer.Deserialize<List<MemoryItem>>(json);

                    _cache = list ?? new List<MemoryItem>();
                }
                catch
                {
                    _cache = new List<MemoryItem>();
                }
            }
        }

        private void SaveUnsafe()
        {
            var json = JsonSerializer.Serialize(_cache, new JsonSerializerOptions
            {
                WriteIndented = true
            });

            File.WriteAllText(_memoryFilePath, json);
        }

        private void TrimUnsafe()
        {
            const int maxTotalItems = 2000;

            if (_cache.Count <= maxTotalItems)
                return;

            _cache = _cache
                .OrderByDescending(x => x.Importance)
                .ThenByDescending(x => x.UpdatedAtUtc)
                .Take(maxTotalItems)
                .ToList();
        }

        private static double Score(MemoryItem item, string fileKey, string agentId, string normalizedQuery)
        {
            double score = 0;

            if (!string.IsNullOrWhiteSpace(fileKey) &&
                string.Equals(item.FileKey, fileKey, StringComparison.OrdinalIgnoreCase))
            {
                score += 3.0;
            }

            if (!string.IsNullOrWhiteSpace(agentId) &&
                string.Equals(item.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
            {
                score += 3.5;
            }

            if (item.IsSharedMemory)
                score += 1.2;

            if (item.Scope == MemoryScope.Node)
                score += 0.5;
            else if (item.Scope == MemoryScope.File)
                score += 1.0;
            else if (item.Scope == MemoryScope.Project)
                score += 0.8;

            string title = Normalize(item.Title);
            string content = Normalize(item.Content);
            string tags = Normalize(string.Join(" ", item.Tags ?? Array.Empty<string>()));

            if (!string.IsNullOrWhiteSpace(normalizedQuery))
            {
                foreach (var token in normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    if (title.Contains(token, StringComparison.Ordinal))
                        score += 2.0;

                    if (content.Contains(token, StringComparison.Ordinal))
                        score += 1.2;

                    if (tags.Contains(token, StringComparison.Ordinal))
                        score += 1.5;
                }
            }

            score += item.Importance;
            return score;
        }

        private static string Normalize(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var chars = text
                .ToLowerInvariant()
                .Select(ch => char.IsWhiteSpace(ch) ? ' ' : ch)
                .ToArray();

            return new string(chars);
        }
    }
}