namespace test
{
    public sealed class NodeModelSelectionService
    {
        // 個人化自訂路由表（由 MainWindow 注入，UI 預覽與實際執行共用同一個物件參考）。
        private NodeTaskRoutingOverrides? _overrides;

        /// <summary>注入個人化「任務 → 模型」自訂路由表。傳入同一個物件參考即可跨 instance 同步。</summary>
        public void UseOverrides(NodeTaskRoutingOverrides overrides)
        {
            _overrides = overrides;
        }

        /// <summary>
        /// 只查使用者自訂 override（不回退到內建規則）。命中且模型仍可用回 true。
        /// 給「API 自動」路徑用：使用者明確釘選時，覆蓋 API resolver 的建議。
        /// </summary>
        public bool TryGetOverride(NodeTaskMode taskMode, out string modelId)
        {
            if (_overrides != null && _overrides.TryGet(taskMode, out var pinned))
            {
                modelId = AiModelHelper.NormalizeNodeModel(pinned);
                return true;
            }

            modelId = "";
            return false;
        }

        public string ResolveRuleAutoModel(
            NodeTaskMode taskMode,
            string? currentSelectedModel)
        {
            // 使用者自訂優先：命中且模型仍可用就用，否則回退到內建預設規則。
            if (_overrides != null && _overrides.TryGet(taskMode, out var overrideModel))
            {
                return AiModelHelper.NormalizeNodeModel(overrideModel);
            }

            return AiModelHelper.NormalizeNodeModel(
                NodeTaskRoutingRegistry.RecommendModel(taskMode, currentSelectedModel));
        }
    }
}
