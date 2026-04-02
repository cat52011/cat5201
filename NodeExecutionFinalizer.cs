using System;

namespace test
{
    public sealed class NodeExecutionFinalizer
    {
        private readonly MainWindow _main;
        private readonly NodeDecisionPresenter _presenter;
        private readonly NodeExecutionLogFactory _logFactory;

        public NodeExecutionFinalizer(
            MainWindow main,
            NodeDecisionPresenter presenter,
            NodeExecutionLogFactory logFactory)
        {
            _main = main;
            _presenter = presenter;
            _logFactory = logFactory;
        }

        public NodeExecutionDecision FinalizeDecision(
            NodeExecutionDecision decision,
            AiFallbackExecutionResult execution)
        {
            decision.ActualModelId = string.IsNullOrWhiteSpace(execution.ActualModelId)
                ? decision.ModelId
                : AiModelHelper.NormalizeNodeModel(execution.ActualModelId);

            decision.RuntimeFallbackUsed = execution.UsedFallback;
            decision.RuntimeFallbackSummary = execution.Summary ?? "";
            decision.RuntimeFallbackAttempts = execution.Attempts ?? Array.Empty<AiFallbackAttempt>();

            return decision;
        }

        public void Present(NodeExecutionDecision decision)
        {
            _presenter.Present(decision);
        }

        public void SyncActualModelToNode(NodeControl node, NodeExecutionDecision decision)
        {
            string actualModel = AiModelHelper.NormalizeNodeModel(
                string.IsNullOrWhiteSpace(decision.ActualModelId)
                    ? decision.ModelId
                    : decision.ActualModelId);

            node.SetCommittedModelId(actualModel, syncEditingModel: true);
            _main.SetNodeSelectedModel(node, actualModel);
            node.RefreshModelSelectionUI();
        }

        public void CommitExecutionLog(
            NodeControl node,
            NodeExecutionDecision decision,
            DateTime startedAtUtc,
            bool success,
            string errorMessage = "")
        {
            var entry = _logFactory.Create(
                node,
                decision,
                startedAtUtc,
                success,
                errorMessage);

            _main.AddExecutionLog(entry);
            _main.RefreshDecisionForNode(node);
        }
    }
}