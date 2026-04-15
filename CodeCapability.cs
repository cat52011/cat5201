using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class CodeCapability : IAgentCapability
    {
        public string Id => "code-capability";

        public AgentCapability RequiredAgentCapability => AgentCapability.CodeTool;

        public bool CanHandle(AgentExecutionContext context)
        {
            if (context == null)
                return false;

            if (context.TaskMode == NodeTaskMode.Code)
                return true;

            string text = context.TopText ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return false;

            return text.Contains("程式") ||
                   text.Contains("程式碼") ||
                   text.Contains("debug", System.StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("bug", System.StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("WPF", System.StringComparison.OrdinalIgnoreCase) ||
                   text.Contains(".NET", System.StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("C#", System.StringComparison.OrdinalIgnoreCase) ||
                   text.Contains("XAML", System.StringComparison.OrdinalIgnoreCase);
        }

        public Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct)
        {
            string augmented =
                context.TopText +
                "\n\n【Capability Code Hint】\n" +
                "請以工程可落地為優先。\n" +
                "若要修改既有系統，優先維持現有結構與命名。\n" +
                "若提供程式碼，請提供可直接貼上的完整方法或完整類別，不要只給片段概念。";

            return Task.FromResult(
                AgentCapabilityResult.WithAugmentedPrompt(augmented));
        }
    }
}