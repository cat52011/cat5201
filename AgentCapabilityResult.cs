using System.Collections.Generic;

namespace test
{
    public sealed class AgentCapabilityResult
    {
        public bool Handled { get; init; }

        public string Output { get; init; } = "";

        public string AugmentedPrompt { get; init; } = "";

        public Dictionary<string, object> Data { get; init; } = new();

        // ✅ NEW：真正工具資料
        public static AgentCapabilityResult WithData(string key, object value)
        {
            return new AgentCapabilityResult
            {
                Handled = true,
                Data = new Dictionary<string, object>
                {
                    [key] = value
                }
            };
        }

        public static AgentCapabilityResult WithAugmentedPrompt(string prompt)
        {
            return new AgentCapabilityResult
            {
                Handled = false,
                AugmentedPrompt = prompt
            };
        }

        public static AgentCapabilityResult DirectHandle(string output)
        {
            return new AgentCapabilityResult
            {
                Handled = true,
                Output = output
            };
        }

        public static AgentCapabilityResult NotHandled()
        {
            return new AgentCapabilityResult
            {
                Handled = false
            };
        }
    }
}