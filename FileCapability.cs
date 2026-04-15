using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class FileCapability : IAgentCapability
    {
        public string Id => "file-capability";

        public AgentCapability RequiredAgentCapability => AgentCapability.FileTool;

        public bool CanHandle(AgentExecutionContext context)
        {
            if (context == null || context.Attachments == null)
                return false;

            return context.Attachments.Any();
        }

        public Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct)
        {
            var attachments = context.Attachments ?? System.Array.Empty<MainWindow.AttachmentInfo>();
            if (attachments.Count == 0)
                return Task.FromResult(AgentCapabilityResult.NotHandled());

            var sb = new StringBuilder();
            sb.AppendLine("【Capability File Hint】");
            sb.AppendLine("本次任務含附件，附件內容屬高優先來源。");
            sb.AppendLine("回答時請優先根據附件內容，而不是憑空補充。");
            sb.AppendLine("附件列表：");

            foreach (var a in attachments)
            {
                sb.AppendLine($"- ({a.Kind}) {a.FileName}");
            }

            string augmented =
                context.TopText +
                "\n\n" +
                sb.ToString();

            return Task.FromResult(
                AgentCapabilityResult.WithAugmentedPrompt(augmented));
        }
    }
}