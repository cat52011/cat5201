using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class ImageCapability : IAgentCapability
    {
        public string Id => "image-capability";

        public AgentCapability RequiredAgentCapability => AgentCapability.ImageTool;

        public bool CanHandle(AgentExecutionContext context)
        {
            if (context == null || context.Attachments == null)
                return false;

            return context.Attachments.Any(a =>
                string.Equals(a.Kind, "image", System.StringComparison.OrdinalIgnoreCase));
        }

        public Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct)
        {
            string augmented =
                context.TopText +
                "\n\n【Capability Image Hint】\n" +
                "本次任務包含圖片附件。\n" +
                "請優先依據圖片本身內容回答，不要忽略圖片。";

            return Task.FromResult(
                AgentCapabilityResult.WithAugmentedPrompt(augmented));
        }
    }
}