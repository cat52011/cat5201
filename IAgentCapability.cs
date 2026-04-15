using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public interface IAgentCapability
    {
        string Id { get; }

        AgentCapability RequiredAgentCapability { get; }

        bool CanHandle(AgentExecutionContext context);

        Task<AgentCapabilityResult> ExecuteAsync(
            AgentExecutionContext context,
            CancellationToken ct);
    }
}