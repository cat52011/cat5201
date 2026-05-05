using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace test
{
    public sealed class AgentParallelRunner
    {
        private readonly Func<AgentExecutionRequest, Task<AgentExecutionResult>> _executeAsync;

        public AgentParallelRunner(
            Func<AgentExecutionRequest, Task<AgentExecutionResult>> executeAsync)
        {
            _executeAsync = executeAsync;
        }

        public async Task<AgentParallelExecutionResult> RunAsync(
            AgentParallelExecutionRequest request,
            CancellationToken ct)
        {
            if (request == null || request.Tasks == null || request.Tasks.Count == 0)
            {
                return new AgentParallelExecutionResult
                {
                    Results = Array.Empty<AgentParallelTaskResult>(),
                    HasAnySuccess = false
                };
            }

            var tasks = request.Tasks.Select(async task =>
            {
                try
                {
                    ct.ThrowIfCancellationRequested();

                    var agent = AgentRegistry.Get(task.AgentId);

                    string input =
$@"【Parallel Agent Task】
Purpose: {task.Purpose}

Instruction:
{task.Instruction}

Original User Request:
{request.OriginalInput}

請只完成你負責的部分，結果會交由 shared workspace 進行最終整合。";

                    var result = await _executeAsync(new AgentExecutionRequest
                    {
                        Node = request.Node,
                        Agent = agent,
                        TopText = input,
                        UseStreaming = false,
                        OnDelta = null,
                        DelegationDepth = 1,
                        ForceAgentProfile = true,
                        Workspace = request.Workspace,
                        CancellationToken = ct
                    });

                    return new AgentParallelTaskResult
                    {
                        AgentId = agent.Id,
                        ModelId = result.Execution?.ActualModelId ?? result.Decision?.ActualModelId ?? "",
                        Output = result.FinalText ?? "",
                        Success = result.IsSuccess,
                        ErrorMessage = result.IsSuccess ? "" : result.Execution?.ErrorMessage ?? ""
                    };
                }
                catch (Exception ex)
                {
                    return new AgentParallelTaskResult
                    {
                        AgentId = task.AgentId,
                        ModelId = "",
                        Output = "",
                        Success = false,
                        ErrorMessage = ex.Message
                    };
                }
            }).ToList();

            var results = await Task.WhenAll(tasks);

            return new AgentParallelExecutionResult
            {
                Results = results.ToList(),
                HasAnySuccess = results.Any(x => x.Success)
            };
        }
    }
}