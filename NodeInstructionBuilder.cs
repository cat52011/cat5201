namespace test
{
    public sealed class NodeInstructionBuilder
    {
        public string BuildGeneralNodeInstructions(string model, NodeTaskMode taskMode)
        {
            return
                "你是一個專業的節點內容生成助手。" +
                "請直接完成目前節點上半部要求的內容，不要先寫任務流程、操作步驟、整理原則、校對流程、備份說明或前言。" +
                "除非使用者明確要求步驟說明，否則請直接輸出結果本身。" +
                "若是翻譯需求，就直接翻譯；若是整理需求，就直接整理完成內容；若是問答需求，就直接回答。" +
                "預設以繁體中文回應；但若節點提示中提供了【使用者偏好】並指定了輸出語言，必須改用該偏好指定的語言。" +
                "可以參考主鏈上下游與支線摘要，但不要被支線帶偏。" +
                "若有附件（圖片/檔案），請閱讀後直接根據附件內容作答。" +
                BuildTaskModeInstruction(taskMode) +
                BuildModelIdentityGuard(model) +
                BuildContinuationEndMarkerInstruction();
        }

        public string BuildPerplexityInstructions(string model, bool isDeepResearch, NodeTaskMode taskMode)
        {
            string baseText = isDeepResearch
                ? "你是一個研究型節點內容助手。請直接輸出整理完成後的內容本身，預設使用繁體中文（若節點提示的【使用者偏好】指定了輸出語言則改用該語言）。不要重述題目，不要輸出前言，不要輸出思考流程。"
                : "你是一個搜尋型節點內容助手。請直接輸出完成結果本身，預設使用繁體中文（若節點提示的【使用者偏好】指定了輸出語言則改用該語言）。不要重述題目，不要輸出前言，不要輸出思考流程。";

            return
                baseText +
                BuildTaskModeInstruction(taskMode) +
                BuildModelIdentityGuard(model) +
                BuildContinuationEndMarkerInstruction();
        }

        public string BuildSegmentDiscoveryInstructions()
        {
            return
                "你是一個文件段落規劃助手。" +
                "請根據附件文件本身的實際內容，按原始順序拆分為適合逐段處理的邏輯段落。" +
                "不要虛構文件不存在的章節，不要加入品牌或模型自我介紹。" +
                "請只輸出合法 JSON，不要輸出 markdown，不要加任何前後說明。";
        }

        public string BuildSegmentTranslationInstructions()
        {
            return
                "你是一個文件分段翻譯助手。" +
                "請直接輸出這一段翻譯完成後的內容本身。" +
                "不要加入前言、摘要、操作說明或步驟。" +
                "若遇到菜單、PDF 或附件，請只翻譯指定段落。" +
                "若模型不確定分段邊界，也不得重複輸出前面已翻過的大段內容。" +
                "不要主動宣稱自己屬於任何特定品牌、公司或模型。";
        }

        private static string BuildModelIdentityGuard(string model)
        {
            var runtimeLabel = GetRuntimeModelLabel(model);

            return
$@"

【模型身分規則】
你目前實際執行的模型是：{runtimeLabel}。
若使用者詢問你是什麼模型、你來自哪一家、或你是否為 OpenAI / Claude / Perplexity，
你必須依照上面這個實際模型名稱誠實回答。
不要把自己說成別的模型，不要把自己統稱為 OpenAI，也不要捏造未提供的型號。
若使用者沒有詢問模型身分，就不要主動提起。";
        }

        private static string BuildContinuationEndMarkerInstruction()
        {
            return "\n\n完整輸出完成後，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private static string BuildTaskModeInstruction(NodeTaskMode taskMode)
        {
            return taskMode switch
            {
                NodeTaskMode.Translate =>
                    "目前任務模式是 Translate。請把重點放在忠實翻譯、原意保留、格式清楚、不要額外延伸評論。",

                NodeTaskMode.Research =>
                    "目前任務模式是 Research。請把重點放在查證、比較、補充背景與整理可信資訊。",

                NodeTaskMode.Summarize =>
                    "目前任務模式是 Summarize。請把重點放在濃縮重點、保留核心資訊、避免冗長。",

                NodeTaskMode.Rewrite =>
                    "目前任務模式是 Rewrite。請把重點放在重寫、潤稿、調整語氣與改善可讀性。",

                NodeTaskMode.Extract =>
                    "目前任務模式是 Extract。請把重點放在抽取欄位、擷取結構化資訊、避免多餘延伸。",

                NodeTaskMode.Code =>
                    "目前任務模式是 Code。請把重點放在程式正確性、可貼上使用、維持既有架構並清楚說明必要修改。",

                _ =>
                    "目前任務模式是 Chat。請直接回應使用者需求並完成內容。"
            };
        }

        private static string GetRuntimeModelLabel(string model)
        {
            var def = AiModelHelper.GetDefinition(model);

            if (!string.IsNullOrWhiteSpace(def.DisplayName))
                return def.DisplayName;

            if (!string.IsNullOrWhiteSpace(def.Id))
                return def.Id;

            return AiModelRegistry.Default.DisplayName;
        }
    }
}