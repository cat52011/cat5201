using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public sealed class AgentCapabilityExecutionPlan
    {
        public IReadOnlyList<string> OrderedCapabilityIds { get; init; } = Array.Empty<string>();
        public IReadOnlySet<string> RequiredCapabilityIds { get; init; } = new HashSet<string>();
        public bool RequiresFreshFacts { get; init; }
        public string Reason { get; init; } = "";

        public bool IsRequired(string capabilityId)
        {
            return RequiredCapabilityIds.Contains(capabilityId);
        }
    }
}