namespace test
{
    public sealed class AgentRuntimeFactory
    {
        private readonly MainWindow _main;
        private readonly NodeExecutionDecisionResolver _decisionResolver;
        private readonly NodeExecutionFinalizer _executionFinalizer;
        private readonly System.Func<NodeControl, string, NodeExecutionDecision, System.Action<string>?, bool, System.Threading.CancellationToken, System.Threading.Tasks.Task<AiFallbackExecutionResult>> _executeWithFallbackAsync;

        public AgentRuntimeFactory(
            MainWindow main,
            NodeExecutionDecisionResolver decisionResolver,
            NodeExecutionFinalizer executionFinalizer,
            System.Func<NodeControl, string, NodeExecutionDecision, System.Action<string>?, bool, System.Threading.CancellationToken, System.Threading.Tasks.Task<AiFallbackExecutionResult>> executeWithFallbackAsync)
        {
            _main = main;
            _decisionResolver = decisionResolver;
            _executionFinalizer = executionFinalizer;
            _executeWithFallbackAsync = executeWithFallbackAsync;
        }

        public AgentRuntime Create()
        {
            return new AgentRuntime(
                _main,
                _decisionResolver,
                _executeWithFallbackAsync,
                _executionFinalizer);
        }
    }
}