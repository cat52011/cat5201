using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    /// <summary>
    /// 個人化「任務 → AI 模型」自訂路由表。使用者可在個人化設定中，為每種任務模式（Research / Code 等）
    /// 指定固定要用的模型，覆蓋 <see cref="NodeTaskRoutingRegistry"/> 的內建預設。
    ///
    /// 單一真相來源：由 MainWindow 持有，並以「同一個物件參考」注入給 UI 預覽與實際執行兩條路徑的
    /// <see cref="NodeModelSelectionService"/>。載入專案時就地更新內容（不換物件），讓兩條路徑永遠同步。
    ///
    /// 只接受 <see cref="AiModelRegistry.IsAvailable"/> 為真的已啟用模型；休眠 / 未知模型一律忽略，
    /// 避免把任務路由到不可用的服務。任一任務模式沒有 override 時，回退到內建預設規則。
    /// </summary>
    public sealed class NodeTaskRoutingOverrides
    {
        private readonly Dictionary<NodeTaskMode, string> _map = new();
        private readonly object _lock = new();

        /// <summary>
        /// 設定某任務模式固定使用的模型。modelId 為 null / 空 → 清除該模式的 override。
        /// 模型未知或未啟用 → 不設定，回 false。成功設定或成功清除回 true。
        /// </summary>
        public bool Set(NodeTaskMode mode, string? modelId)
        {
            mode = NodeTaskModeHelper.Normalize(mode);

            lock (_lock)
            {
                if (string.IsNullOrWhiteSpace(modelId))
                {
                    _map.Remove(mode);
                    return true;
                }

                string normalized = AiModelHelper.NormalizeNodeModel(modelId);
                if (!AiModelRegistry.IsAvailable(normalized))
                    return false;

                _map[mode] = normalized;
                return true;
            }
        }

        public void Clear(NodeTaskMode mode)
        {
            mode = NodeTaskModeHelper.Normalize(mode);
            lock (_lock)
            {
                _map.Remove(mode);
            }
        }

        public void ClearAll()
        {
            lock (_lock)
            {
                _map.Clear();
            }
        }

        /// <summary>取得某任務模式的 override；命中且模型仍可用才回 true。</summary>
        public bool TryGet(NodeTaskMode mode, out string modelId)
        {
            mode = NodeTaskModeHelper.Normalize(mode);
            lock (_lock)
            {
                if (_map.TryGetValue(mode, out var value) && AiModelRegistry.IsAvailable(value))
                {
                    modelId = value;
                    return true;
                }
            }

            modelId = "";
            return false;
        }

        /// <summary>目前所有 override 的快照（給 UI 顯示用）。</summary>
        public IReadOnlyDictionary<NodeTaskMode, string> Snapshot()
        {
            lock (_lock)
            {
                return new Dictionary<NodeTaskMode, string>(_map);
            }
        }

        public bool HasAny
        {
            get { lock (_lock) { return _map.Count > 0; } }
        }

        // ---- 持久化（存於 AppState，序列化為 string→string）----

        /// <summary>序列化為可存檔的字典（key = 任務模式名稱，value = modelId）。</summary>
        public Dictionary<string, string> ToStorage()
        {
            lock (_lock)
            {
                return _map.ToDictionary(
                    kv => kv.Key.ToString(),
                    kv => kv.Value,
                    StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <summary>
        /// 從存檔就地還原（先清空再重填，保留同一個物件參考）。
        /// 無法解析的任務模式名稱或不可用的模型一律跳過。
        /// </summary>
        public void LoadFromStorage(Dictionary<string, string>? data)
        {
            lock (_lock)
            {
                _map.Clear();
                if (data == null)
                    return;

                foreach (var kv in data)
                {
                    if (!Enum.TryParse<NodeTaskMode>(kv.Key, ignoreCase: true, out var mode))
                        continue;

                    mode = NodeTaskModeHelper.Normalize(mode);

                    string normalized = AiModelHelper.NormalizeNodeModel(kv.Value);
                    if (!AiModelRegistry.IsAvailable(normalized))
                        continue;

                    _map[mode] = normalized;
                }
            }
        }
    }
}
