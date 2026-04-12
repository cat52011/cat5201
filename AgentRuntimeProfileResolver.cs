namespace test
{
    public sealed class AgentRuntimeProfileResolver
    {
        public AgentRuntimeProfile Resolve(
            AgentDefinition agent,
            string? preferredModelId = null,
            NodeTaskMode? preferredTaskMode = null)
        {
            var model = string.IsNullOrWhiteSpace(preferredModelId)
                ? agent.DefaultModelId
                : AiModelHelper.NormalizeNodeModel(preferredModelId);

            var taskMode = preferredTaskMode.HasValue
                ? NodeTaskModeHelper.Normalize(preferredTaskMode.Value)
                : NodeTaskModeHelper.Normalize(agent.DefaultTaskMode);

            return new AgentRuntimeProfile
            {
                AgentId = agent.Id,
                RuntimeModelId = model,
                RuntimeTaskMode = taskMode,
                SystemPrompt = agent.SystemPrompt ?? ""
            };
        }
    }
}