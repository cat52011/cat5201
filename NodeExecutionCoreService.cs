using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class NodeExecutionCoreService
    {
        private readonly AiServiceRouter _router;
        private readonly NodeContextStrategyResolver _contextStrategyResolver;
        private readonly NodePromptBuilder _promptBuilder;
        private readonly NodeInstructionBuilder _instructionBuilder;
        private readonly NodeTextProcessingService _textProcessing;
        private readonly Func<string, string, int, bool, int, CancellationToken, Task<AiRequest>> _buildRequestFactory;

        private readonly int _continuationMaxRounds;
        private readonly int _mainReplyMaxOutputTokens;
        private readonly NodeMemoryService _memoryService;
        public NodeExecutionCoreService(
    AiServiceRouter router,
    NodeContextStrategyResolver contextStrategyResolver,
    NodePromptBuilder promptBuilder,
    NodeInstructionBuilder instructionBuilder,
    NodeTextProcessingService textProcessing,
    NodeMemoryService memoryService,
    Func<string, string, int, bool, int, CancellationToken, Task<AiRequest>> buildRequestFactory,
    int continuationMaxRounds,
    int mainReplyMaxOutputTokens)
        {
            _router = router;
            _contextStrategyResolver = contextStrategyResolver;
            _promptBuilder = promptBuilder;
            _instructionBuilder = instructionBuilder;
            _textProcessing = textProcessing;
            _memoryService = memoryService;
            _buildRequestFactory = buildRequestFactory;
            _continuationMaxRounds = continuationMaxRounds;
            _mainReplyMaxOutputTokens = mainReplyMaxOutputTokens;
        }
        public async Task<string> ExecuteAsync(
    NodeControl currentNode,
    string agentId,
    string topText,
    string model,
    NodeTaskMode taskMode,
    CancellationToken ct)
        {
            var memory = ShouldSuppressMemoryForFreshFinance(topText, taskMode)
                ? new MemoryQueryResult()
                : _memoryService.RecallRelevant(currentNode, agentId, topText, taskMode);
            string prompt = _promptBuilder.BuildPrompt(new NodePromptBuildRequest
            {
                CurrentNode = currentNode,
                TopText = topText,
                TaskMode = taskMode,
                Strategy = _contextStrategyResolver.Resolve(model, taskMode),
                MemoryBlock = memory.PromptBlock,
                PreferenceBlock = memory.PreferenceBlock
            });

            return await GenerateWithContinuationAsync(
                model,
                async followUp => await _buildRequestFactory(
                    model,
                    prompt + followUp,
                    (int)taskMode,
                    false,
                    _mainReplyMaxOutputTokens,
                    ct),
                ct);
        }
        public async Task<string> ExecuteStreamAsync(
     NodeControl currentNode,
     string agentId,
     string topText,
     string model,
     NodeTaskMode taskMode,
     Action<string> onDelta,
     CancellationToken ct)
        {
            var memory = ShouldSuppressMemoryForFreshFinance(topText, taskMode)
                ? new MemoryQueryResult()
                : _memoryService.RecallRelevant(currentNode, agentId, topText, taskMode);
            string prompt = _promptBuilder.BuildPrompt(new NodePromptBuildRequest
            {
                CurrentNode = currentNode,
                TopText = topText,
                TaskMode = taskMode,
                Strategy = _contextStrategyResolver.Resolve(model, taskMode),
                MemoryBlock = memory.PromptBlock,
                PreferenceBlock = memory.PreferenceBlock
            });

            return await GenerateWithContinuationStreamingAsync(
                model,
                async followUp => await _buildRequestFactory(
                    model,
                    prompt + followUp,
                    (int)taskMode,
                    true,
                    _mainReplyMaxOutputTokens,
                    ct),
                onDelta,
                ct);
        }

        private async Task<string> GenerateWithContinuationAsync(
    string model,
    Func<string, Task<AiRequest>> buildRequestFactory,
    CancellationToken ct)
        {
            var finalText = new StringBuilder();
            var provider = _router.GetProvider(model);

            for (int round = 0; round < _continuationMaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                string followUp = round == 0
                    ? ""
                    : $@"

【你前一次已輸出的內容（不可重複，僅供接續）】
{finalText}

請直接從上一行未完成處繼續輸出。
不要重複前面內容。
若這次已完整完成，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";

                var request = await buildRequestFactory(followUp);
                var response = await provider.GenerateAsync(request, ct);
                string reply = response.Text;

                if (string.IsNullOrWhiteSpace(reply))
                    break;

                bool ended = _textProcessing.HasEndMarker(reply);
                string cleaned = _textProcessing.RemoveEndMarker(reply);

                if (round == 0)
                {
                    finalText.Append(cleaned.Trim());
                }
                else
                {
                    var append = _textProcessing.RemoveLeadingOverlap(finalText.ToString(), cleaned);
                    append = _textProcessing.RemoveRepeatedBlocks(append);

                    if (!string.IsNullOrWhiteSpace(append) &&
                        !_textProcessing.IsHighlySimilarByContainment(finalText.ToString(), append))
                    {
                        if (finalText.Length > 0 && !finalText.ToString().EndsWith("\n"))
                            finalText.AppendLine();

                        finalText.Append(append.Trim());
                    }
                }

                if (ended)
                    break;
            }

            return _textProcessing.RemoveRepeatedBlocks(finalText.ToString().Trim());
        }

        private static bool ShouldSuppressMemoryForFreshFinance(string text, NodeTaskMode taskMode)
        {
            if (taskMode == NodeTaskMode.Translate ||
                taskMode == NodeTaskMode.Rewrite ||
                taskMode == NodeTaskMode.Summarize)
            {
                return false;
            }

            return FinanceTaskDetector.IsFinanceLike(text);
        }

        private async Task<string> GenerateWithContinuationStreamingAsync(
    string model,
    Func<string, Task<AiRequest>> buildRequestFactory,
    Action<string> onDelta,
    CancellationToken ct)
        {
            var finalText = new StringBuilder();
            var provider = _router.GetProvider(model);

            for (int round = 0; round < _continuationMaxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();

                string followUp = round == 0
                    ? ""
                    : $@"

【你前一次已輸出的內容（不可重複，僅供接續）】
{finalText}

請直接從上一行未完成處繼續輸出。
不要重複前面內容。
若這次已完整完成，請在最後一行單獨輸出 [[END_OF_RESPONSE]]。";

                var request = await buildRequestFactory(followUp);
                string reply;

                if (round == 0)
                {
                    var streamed = await provider.GenerateStreamAsync(request, delta => onDelta?.Invoke(delta), ct);
                    reply = streamed.Text;
                }
                else
                {
                    var normal = await provider.GenerateAsync(request, ct);
                    reply = normal.Text;
                }

                if (string.IsNullOrWhiteSpace(reply))
                    break;

                bool ended = _textProcessing.HasEndMarker(reply);
                string cleaned = _textProcessing.RemoveEndMarker(reply);

                if (round == 0)
                {
                    finalText.Append(cleaned.Trim());
                }
                else
                {
                    var append = _textProcessing.RemoveLeadingOverlap(finalText.ToString(), cleaned);
                    append = _textProcessing.RemoveRepeatedBlocks(append);

                    if (!string.IsNullOrWhiteSpace(append) &&
                        !_textProcessing.IsHighlySimilarByContainment(finalText.ToString(), append))
                    {
                        if (finalText.Length > 0 && !finalText.ToString().EndsWith("\n"))
                        {
                            finalText.AppendLine();
                            onDelta?.Invoke(Environment.NewLine);
                        }

                        finalText.Append(append.Trim());
                        onDelta?.Invoke(append.Trim());
                    }
                }

                if (ended)
                    break;
            }

            return _textProcessing.RemoveRepeatedBlocks(finalText.ToString().Trim());
        }
    }
}
