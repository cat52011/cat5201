using System;
using System.Linq;

namespace test
{
    public sealed class NodeExecutionHeuristicsService
    {
        private readonly MainWindow _main;

        public NodeExecutionHeuristicsService(MainWindow main)
        {
            _main = main;
        }

        public bool LooksLikeFullTranslationRequest(string topText)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return false;

            string raw = topText.Trim();
            string normalized = raw.ToLowerInvariant();

            string[] keywords =
            {
                "完整翻譯", "全部翻譯", "完整中文", "完整菜單", "整份翻譯", "全文翻譯",
                "翻譯整份", "按格式翻譯", "按照格式翻譯",
                "translate the whole", "full translation", "translate all", "entire document"
            };

            return keywords.Any(k =>
                raw.Contains(k, StringComparison.OrdinalIgnoreCase) ||
                normalized.Contains(k.ToLowerInvariant(), StringComparison.Ordinal));
        }

        public bool HasNonImageAttachments(NodeControl node)
        {
            var atts = _main.GetAttachmentsForNode(node);
            return atts.Any(a => !string.Equals(a.Kind, "image", StringComparison.OrdinalIgnoreCase));
        }
    }
}