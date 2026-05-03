using System.Collections.Generic;

namespace test
{
    public sealed class TaskDecompositionPayload
    {
        public IReadOnlyList<TaskDecompositionStep> Steps { get; init; }
            = new List<TaskDecompositionStep>();

        public string Summary { get; init; } = "";
    }

    public sealed class TaskDecompositionStep
    {
        public int Order { get; init; }

        public string StepType { get; init; } = "";
        // search / summarize / reason / predict / compare / answer

        public string Goal { get; init; } = "";

        public string RequiredInput { get; init; } = "";

        public string OutputExpectation { get; init; } = "";
    }
}