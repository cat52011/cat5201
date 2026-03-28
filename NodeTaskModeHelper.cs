using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public sealed class NodeTaskModeOption
    {
        public NodeTaskMode Value { get; init; }
        public string DisplayName { get; init; } = "";
    }

    public static class NodeTaskModeHelper
    {
        private static readonly IReadOnlyList<NodeTaskModeOption> _all = new[]
        {
            new NodeTaskModeOption { Value = NodeTaskMode.Chat,      DisplayName = "Chat" },
            new NodeTaskModeOption { Value = NodeTaskMode.Research,  DisplayName = "Research" },
            new NodeTaskModeOption { Value = NodeTaskMode.Translate, DisplayName = "Translate" },
            new NodeTaskModeOption { Value = NodeTaskMode.Summarize, DisplayName = "Summarize" },
            new NodeTaskModeOption { Value = NodeTaskMode.Rewrite,   DisplayName = "Rewrite" },
            new NodeTaskModeOption { Value = NodeTaskMode.Extract,   DisplayName = "Extract" },
            new NodeTaskModeOption { Value = NodeTaskMode.Code,      DisplayName = "Code" }
        };

        public static IReadOnlyList<NodeTaskModeOption> All => _all;

        public static NodeTaskMode Default => NodeTaskMode.Chat;

        public static NodeTaskMode Normalize(NodeTaskMode mode)
        {
            return Enum.IsDefined(typeof(NodeTaskMode), mode)
                ? mode
                : Default;
        }

        public static NodeTaskMode ParseOrDefault(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw))
                return Default;

            return Enum.TryParse<NodeTaskMode>(raw.Trim(), true, out var parsed)
                ? Normalize(parsed)
                : Default;
        }

        public static string ToStorageValue(NodeTaskMode mode)
        {
            return Normalize(mode).ToString();
        }

        public static string ToDisplayName(NodeTaskMode mode)
        {
            mode = Normalize(mode);

            var hit = _all.FirstOrDefault(x => x.Value == mode);
            return hit?.DisplayName ?? "Chat";
        }

        public static NodeTaskModeOption GetOption(NodeTaskMode mode)
        {
            mode = Normalize(mode);

            return _all.FirstOrDefault(x => x.Value == mode)
                   ?? _all.First(x => x.Value == Default);
        }
    }
}