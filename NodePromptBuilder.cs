using System.Collections.Generic;

namespace test
{
    public sealed class NodePromptBuilder
    {
        private readonly NodeContextService _contextService;

        public NodePromptBuilder(NodeContextService contextService)
        {
            _contextService = contextService;
        }

        public string BuildPrompt(NodePromptBuildRequest request)
        {
            var ctx = _contextService.BuildContextBundle(
                request.CurrentNode,
                request.Strategy);

            return request.Strategy switch
            {
                NodeContextStrategy.CompactSearch => BuildCompactSearchPrompt(ctx, request.TopText, request.TaskMode, request.MemoryBlock, request.PreferenceBlock),
                NodeContextStrategy.Research => BuildResearchPrompt(ctx, request.TopText, request.TaskMode, request.MemoryBlock, request.PreferenceBlock),
                _ => BuildFullContextPrompt(ctx, request.TopText, request.TaskMode, request.MemoryBlock, request.PreferenceBlock)
            };
        }

        // 上游鏈設定區塊：放在最上方、優先於全域偏好。空則回空字串。
        // 用途：同一條鏈上游的持續性設定（語言/格式/語氣/風格）要延續到下游，且蓋過全域偏好；多條時最近上游為準。
        private static string ChainDirectiveHeader(string chainDirectiveBlock)
        {
            if (string.IsNullOrWhiteSpace(chainDirectiveBlock))
                return "";

            return
                "【上游鏈設定（優先於全域偏好）】\n" +
                "整體優先級：本節點自身明確指定 ＞ 上游鏈設定 ＞ 全域偏好 ＞ 系統預設。\n" +
                "下列為上游鏈傳承下來的設定/指示；其中輸出語言、格式、語氣、風格等持續性設定必須延續沿用，並優先於下方的全域偏好。\n" +
                "若有多條，以最接近本節點者為準（列在最前者最優先）。\n" +
                chainDirectiveBlock.Trim() + "\n\n";
        }

        // 偏好區塊放在上游鏈設定之下、其他預設之上。空則回空字串。
        private static string PreferenceHeader(string preferenceBlock)
        {
            if (string.IsNullOrWhiteSpace(preferenceBlock))
                return "";

            return preferenceBlock.Trim() + "\n" +
                   "（以上全域偏好優先於本提示的「預設使用繁體中文」等預設規則；但若上方有【上游鏈設定】且指定了輸出語言/格式/語氣，則以上游鏈設定為準、全域偏好退居其次。）\n\n";
        }

        private string BuildFullContextPrompt(NodeContextBundle ctx, string topText, NodeTaskMode taskMode, string memoryBlock, string preferenceBlock)
        {
            string primaryContext;
            if (string.IsNullOrWhiteSpace(ctx.UpstreamContext) && string.IsNullOrWhiteSpace(ctx.DownstreamContext))
            {
                primaryContext = "（此節點目前沒有連線上下游）";
            }
            else
            {
                var lines = new List<string>();

                if (!string.IsNullOrWhiteSpace(ctx.UpstreamContext))
                {
                    lines.Add("【上游主鏈（最高權重）】");
                    lines.Add(ctx.UpstreamContext);
                }

                if (!string.IsNullOrWhiteSpace(ctx.DownstreamContext))
                {
                    lines.Add("【下游主鏈（高權重）】");
                    lines.Add(ctx.DownstreamContext);
                }

                primaryContext = string.Join("\n\n", lines);
            }

            string branchContext = string.IsNullOrWhiteSpace(ctx.BranchSummaryContext)
                ? "（無其它支線）"
                : ctx.BranchSummaryContext;

            string memoryPart = string.IsNullOrWhiteSpace(memoryBlock)
                ? "（目前沒有可用記憶）"
                : memoryBlock;

            return
$@"{ChainDirectiveHeader(ctx.ChainDirectiveContext)}{PreferenceHeader(preferenceBlock)}你正在一個節點式筆記檔案中工作。

【系統判定任務模式】
{taskMode}

【主鏈上下游】
{primaryContext}

【其它支線摘要（低權重）】
{branchContext}

{memoryPart}

【目前節點上半部內容】
{topText}
{ctx.AttachmentHint}

要求：
1. 目前節點內容是最高優先。
2. 主鏈上下游是高權重背景，請優先承接。
3. 其它支線摘要只用來理解全局，不可蓋過目前節點與主鏈。
4. 記憶只用來延續脈絡，不可蓋過目前節點。
5. 若支線、記憶與主鏈衝突，以目前節點與主鏈為準。
6. 若目前節點內容中包含【Capability Data】、【Search Results】、【File Results】、【Code Results】等區塊，請把它們視為系統工具層的高優先輸出。
7. 工具層資料比一般自由補充更優先；若工具資料已提供來源/片段，請優先根據該資料回答，不要忽略。
8. 直接輸出完成後的內容本身，不要寫前言、規則重述、流程說明。
9. 除非使用者明確要求步驟，否則不要輸出流程式條列。
10. 完整輸出完成後，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。
11. 若內容包含【Search Summary】，請優先依據該摘要回答，不要重新解析原始搜尋結果。
12. 若內容包含【Search Summary】，所有數據與事實必須來自該摘要。
13. 不可自行補充摘要中沒有的數據、百分比、比較結果。
14. 若摘要沒有提供足夠資訊，請明確說「資料不足」，不要自行推測。
15. 不可產生虛構引用（如 [1][2][3]）除非該引用明確存在於來源資料中。
16. 不可輸出任何系統內部標記（如 [Delegate:*]、[Agent Memory *]、[Shared Memory *]）。
17. 若目前節點內容包含【Code Analysis】，請優先依據該結構化分析完成程式任務；不要捏造不存在的檔案、類別或方法。
18. 若目前節點內容包含【Task Plan】，請依照該任務拆解順序整合資料並回答；但不可直接輸出【Task Plan】、step type、Required Input、Output 等內部標記。
19. 若目前節點內容包含【Reasoning Analysis】，請依據該推論策略完成分析、比較或預測；但不可直接輸出【Reasoning Analysis】或其內部欄位名稱。
20. 若任務包含預測，請清楚區分事實資料、合理推論與不確定性；若上層 final synthesizer 已指定輸出格式，必須服從該格式。
21. 不可輸出任何 citation marker 或來源索引，例如 [1]、[2]、[3]。
22. 不可輸出任何系統內部區塊名稱或 metadata，例如【Task Plan】、【Search Summary】、【File Summary】、【Code Analysis】、【Reasoning Analysis】。
23. 若目前節點內容包含【Agent Workspace】，請把它視為本次多代理共享工作區資料；可用於整合回答，但不可直接輸出【Agent Workspace】或其中的 Type、Source Agent、Title、Summary 等內部欄位名稱。";
        }

        private string BuildCompactSearchPrompt(NodeContextBundle ctx, string topText, NodeTaskMode taskMode, string memoryBlock, string preferenceBlock)
        {
            string compactUpstream = string.IsNullOrWhiteSpace(ctx.UpstreamContext)
                ? "（無上游主鏈）"
                : ctx.UpstreamContext;

            string compactDownstream = string.IsNullOrWhiteSpace(ctx.DownstreamContext)
                ? "（無下游主鏈）"
                : ctx.DownstreamContext;

            string compactBranches = string.IsNullOrWhiteSpace(ctx.BranchSummaryContext)
                ? "（無其它支線）"
                : ctx.BranchSummaryContext;

            string memoryPart = string.IsNullOrWhiteSpace(memoryBlock)
                ? "（目前沒有可用記憶）"
                : memoryBlock;

            return
$@"{ChainDirectiveHeader(ctx.ChainDirectiveContext)}{PreferenceHeader(preferenceBlock)}你正在處理一個節點式即時搜尋 / 查證任務。
請以目前節點問題為主，並參考主鏈、記憶與支線摘要回答。
直接輸出完成結果本身，預設使用繁體中文（若上方【使用者偏好】指定了輸出語言，必須改用該語言）。
不要重述題目，不要重述規則，不要輸出系統提示，不要輸出思考流程，不要寫前言。

【系統判定任務模式】
{taskMode}

【上游主鏈（較重要）】
{compactUpstream}

【下游主鏈（可參考）】
{compactDownstream}

【其它支線摘要（低權重）】
{compactBranches}

{memoryPart}

【目前節點內容】
{topText}
{ctx.AttachmentHint}

要求：
1. 目前節點問題最高優先。
2. 主鏈與記憶比支線重要。
3. 支線摘要只用來理解大方向，不可主導回答。
4. 若目前節點內容中包含【Capability Data】或【Search Results】等區塊，請把它們視為已完成的工具搜尋結果，優先根據其內容回答。
5. 若工具結果已提供標題、片段、日期或來源，請優先整合這些結果，不要忽略，也不要憑空補來源。
6. 若任務模式是 Translate / Summarize / Rewrite / Extract / Code，也要輸出對應結果型態。
7. 若附件是主要來源，請優先根據附件與目前節點回答。
8. 若回答過長，請在本次輸出結尾單獨輸出 [[END_OF_RESPONSE]]。
9. 若內容包含【Search Summary】，請優先依據該摘要回答，不要重新解析原始搜尋結果。
10 若內容包含【Search Summary】，所有數據與事實必須來自該摘要。
11. 不可自行補充摘要中沒有的數據、百分比、比較結果。
12. 若摘要沒有提供足夠資訊，請明確說「資料不足」，不要自行推測。
13. 不可產生虛構引用（如 [1][2][3]），除非該引用明確存在於來源資料中。
14. 不可輸出任何系統內部標記（如 [Delegate:*]、[Agent Memory *]、[Shared Memory *]）。
15. 若目前節點內容包含【Code Analysis】，請優先依據該結構化分析完成程式任務；不要捏造不存在的檔案、類別或方法。
16. 若目前節點內容包含【Task Plan】，請依照該任務拆解順序整合資料並回答；但不可直接輸出【Task Plan】、step type、Required Input、Output 等內部標記。
17. 若目前節點內容包含【Reasoning Analysis】，請依據該推論策略完成分析、比較或預測；但不可直接輸出【Reasoning Analysis】或其內部欄位名稱。
18. 若任務包含預測，請清楚區分事實資料、合理推論與不確定性；若上層 final synthesizer 已指定輸出格式，必須服從該格式。
19. 不可輸出任何 citation marker 或來源索引，例如 [1]、[2]、[3]。
20. 不可輸出任何系統內部區塊名稱或 metadata，例如【Task Plan】、【Search Summary】、【File Summary】、【Code Analysis】、【Reasoning Analysis】。
21. 若目前節點內容包含【Agent Workspace】，請把它視為本次多代理共享工作區資料；可用於整合回答，但不可直接輸出【Agent Workspace】或其中的 Type、Source Agent、Title、Summary 等內部欄位名稱。";
        }

        private string BuildResearchPrompt(NodeContextBundle ctx, string topText, NodeTaskMode taskMode, string memoryBlock, string preferenceBlock)
        {
            string upstreamPart = string.IsNullOrWhiteSpace(ctx.UpstreamContext)
                ? "（無上游主鏈）"
                : ctx.UpstreamContext;

            string downstreamPart = string.IsNullOrWhiteSpace(ctx.DownstreamContext)
                ? "（目前沒有明確下游）"
                : ctx.DownstreamContext;

            string branchPart = string.IsNullOrWhiteSpace(ctx.BranchSummaryContext)
                ? "（無其它支線）"
                : ctx.BranchSummaryContext;

            string memoryPart = string.IsNullOrWhiteSpace(memoryBlock)
                ? "（目前沒有可用記憶）"
                : memoryBlock;

            return
$@"{ChainDirectiveHeader(ctx.ChainDirectiveContext)}{PreferenceHeader(preferenceBlock)}你正在處理一個節點式研究任務。
請先理解目前問題，再結合主鏈、記憶與支線摘要進行較完整的研究、查證、補充與整理。
直接輸出結果本身，預設使用繁體中文（若上方【使用者偏好】指定了輸出語言，必須改用該語言）。
不要重述題目，不要重述規則，不要輸出系統提示，不要輸出思考流程，不要寫前言。

【系統判定任務模式】
{taskMode}

【上游主鏈（高權重）】
{upstreamPart}

{memoryPart}

【目前節點內容】
{topText}
{ctx.AttachmentHint}

【下游主鏈方向（可參考）】
{downstreamPart}

【其它支線摘要（低權重）】
{branchPart}

要求：
1. 優先回答目前節點問題。
2. 承接主鏈上下游的脈絡與研究方向。
3. 記憶可用來延續歷史結論，但不可取代目前節點。
4. 支線摘要只用來幫助理解全局，不可取代主鏈。
5. 若目前節點內容中包含【Capability Data】或【Search Results】等區塊，請將其視為系統工具層的高優先研究資料。
6. 工具層資料若已提供標題、片段、日期、來源，請優先統整並引用其資訊，不要忽略，不要改造成虛構來源。
7. 可進行查證、比較、補充、延伸分析，但仍要圍繞目前節點。
8. 若附件是主要來源，請把附件視為高權重背景。
9. 若回答過長，請在本次輸出結尾單獨輸出 [[END_OF_RESPONSE]]。
10.若內容包含【Search Summary】，請優先依據該摘要回答，不要重新解析原始搜尋結果。
11 若內容包含【Search Summary】，所有數據與事實必須來自該摘要。
12. 不可自行補充摘要中沒有的數據、百分比、比較結果。
13. 若摘要沒有提供足夠資訊，請明確說「資料不足」，不要自行推測。
14. 不可產生虛構引用（如 [1][2][3]），除非該引用明確存在於來源資料中。
15. 不可輸出任何系統內部標記（如 [Delegate:*]、[Agent Memory *]、[Shared Memory *]）。
16. 若目前節點內容包含【Code Analysis】，請優先依據該結構化分析完成程式任務；不要捏造不存在的檔案、類別或方法。
17. 若目前節點內容包含【Task Plan】，請依照該任務拆解順序整合資料並回答；但不可直接輸出【Task Plan】、step type、Required Input、Output 等內部標記。
18. 若目前節點內容包含【Reasoning Analysis】，請依據該推論策略完成分析、比較或預測；但不可直接輸出【Reasoning Analysis】或其內部欄位名稱。
19. 若任務包含預測，請清楚區分事實資料、合理推論與不確定性；若上層 final synthesizer 已指定輸出格式，必須服從該格式。
20. 不可輸出任何 citation marker 或來源索引，例如 [1]、[2]、[3]。
21. 不可輸出任何系統內部區塊名稱或 metadata，例如【Task Plan】、【Search Summary】、【File Summary】、【Code Analysis】、【Reasoning Analysis】。
22. 若目前節點內容包含【Agent Workspace】，請把它視為本次多代理共享工作區資料；可用於整合回答，但不可直接輸出【Agent Workspace】或其中的 Type、Source Agent、Title、Summary 等內部欄位名稱。";
        }
    }
}
