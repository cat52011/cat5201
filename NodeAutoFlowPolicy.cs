namespace test
{
    public sealed class NodeAutoFlowPolicy
    {
        public bool AutoRunEnabled { get; init; } = true;

        // 第一版先保留欄位，後面再真的啟用
        public bool WaitForAllInputs { get; init; } = false;
        public bool StopFlowOnError { get; init; } = true;
        public bool AllowPartialInput { get; init; } = false;

        public static NodeAutoFlowPolicy Default { get; } = new();
    }
}