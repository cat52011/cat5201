namespace test
{
    public sealed class AgentCapabilityResult
    {
        public bool Handled { get; init; }

        public string Output { get; init; } = "";

        public string AugmentedPrompt { get; init; } = "";

        public static AgentCapabilityResult NotHandled()
        {
            return new AgentCapabilityResult
            {
                Handled = false
            };
        }

        public static AgentCapabilityResult FromOutput(string output)
        {
            return new AgentCapabilityResult
            {
                Handled = true,
                Output = output ?? ""
            };
        }

        public static AgentCapabilityResult WithAugmentedPrompt(string augmentedPrompt)
        {
            return new AgentCapabilityResult
            {
                Handled = false,
                AugmentedPrompt = augmentedPrompt ?? ""
            };
        }
    }
}