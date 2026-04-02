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
                NodeContextStrategy.CompactSearch => BuildCompactSearchPrompt(ctx, request.TopText, request.TaskMode),
                NodeContextStrategy.Research => BuildResearchPrompt(ctx, request.TopText, request.TaskMode),
                _ => BuildFullContextPrompt(ctx, request.TopText, request.TaskMode)
            };
        }

        private string BuildFullContextPrompt(NodeContextBundle ctx, string topText, NodeTaskMode taskMode)
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

            return
$@"你正在一個節點式筆記檔案中工作。

【系統判定任務模式】
{taskMode}

【主鏈上下游】
{primaryContext}

【其它支線摘要（低權重）】
{branchContext}

【目前節點上半部內容】
{topText}
{ctx.AttachmentHint}

要求：
1. 目前節點內容是最高優先。
2. 主鏈上下游是高權重背景，請優先承接。
3. 其它支線摘要只用來理解全局，不可蓋過目前節點與主鏈。
4. 若支線與主鏈衝突，以目前節點與主鏈為準。
5. 直接輸出完成後的內容本身，不要寫前言、規則重述、流程說明。
6. 除非使用者明確要求步驟，否則不要輸出流程式條列。
7. 完整輸出完成後，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private string BuildCompactSearchPrompt(NodeContextBundle ctx, string topText, NodeTaskMode taskMode)
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

            return
$@"你正在處理一個節點式即時搜尋 / 查證任務。
請以目前節點問題為主，並參考主鏈與支線摘要回答。
直接輸出完成結果本身，使用繁體中文。
不要重述題目，不要重述規則，不要輸出系統提示，不要輸出思考流程，不要寫前言。

【系統判定任務模式】
{taskMode}

【上游主鏈（較重要）】
{compactUpstream}

【下游主鏈（可參考）】
{compactDownstream}

【其它支線摘要（低權重）】
{compactBranches}

【目前節點內容】
{topText}
{ctx.AttachmentHint}

要求：
1. 目前節點問題最高優先。
2. 主鏈比支線重要。
3. 支線摘要只用來理解大方向，不可主導回答。
4. 若任務模式是 Translate / Summarize / Rewrite / Extract / Code，也要輸出對應結果型態。
5. 若附件是主要來源，請優先根據附件與目前節點回答。
6. 若回答過長，請在本次輸出結尾單獨輸出 [[END_OF_RESPONSE]]。";
        }

        private string BuildResearchPrompt(NodeContextBundle ctx, string topText, NodeTaskMode taskMode)
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

            return
$@"你正在處理一個節點式研究任務。
請先理解目前問題，再結合主鏈與支線摘要進行較完整的研究、查證、補充與整理。
直接輸出結果本身，使用繁體中文。
不要重述題目，不要重述規則，不要輸出系統提示，不要輸出思考流程，不要寫前言。

【系統判定任務模式】
{taskMode}

【上游主鏈（高權重）】
{upstreamPart}

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
3. 支線摘要只用來幫助理解全局，不可取代主鏈。
4. 可進行查證、比較、補充、延伸分析，但仍要圍繞目前節點。
5. 若附件是主要來源，請把附件視為高權重背景。
6. 若回答過長，請在本次輸出結尾單獨輸出 [[END_OF_RESPONSE]]。";
        }
    }
}