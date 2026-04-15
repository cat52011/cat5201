using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public static class AgentCapabilityRegistry
    {
        private static readonly List<IAgentCapability> _all = new();

        public static IReadOnlyList<IAgentCapability> All => _all;

        public static void Register(IAgentCapability capability)
        {
            if (capability == null)
                return;

            if (_all.Any(x => string.Equals(x.Id, capability.Id, StringComparison.OrdinalIgnoreCase)))
                return;

            _all.Add(capability);
        }

        public static void Clear()
        {
            _all.Clear();
        }
    }
}