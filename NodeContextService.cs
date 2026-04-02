using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace test
{
    public sealed class NodeContextService
    {
        private readonly MainWindow _main;

        public NodeContextService(MainWindow main)
        {
            _main = main;
        }

        public NodeContextBundle BuildContextBundle(NodeControl current, NodeContextStrategy strategy)
        {
            return strategy switch
            {
                NodeContextStrategy.CompactSearch => BuildCompactSearchContextBundle(current),
                NodeContextStrategy.Research => BuildResearchContextBundle(current),
                _ => BuildFullContextBundle(current)
            };
        }

        private NodeContextBundle BuildFullContextBundle(NodeControl current)
        {
            var bundle = CreateBaseContextBundle(current);

            var upstream = CollectUpstream(current, 20);
            var downstream = CollectDownstream(current, 6);

            bundle.UpstreamContext = BuildContextSection(
                upstream,
                topLimit: 1200,
                bottomLimit: 1200,
                maxCount: int.MaxValue);

            bundle.DownstreamContext = BuildContextSection(
                downstream,
                topLimit: 700,
                bottomLimit: 700,
                maxCount: int.MaxValue);

            bundle.BranchSummaryContext = BuildBranchSummaryContext(
                current,
                upstream,
                downstream,
                representativeCountPerBranch: 3,
                summaryTopLimit: 120,
                summaryBottomLimit: 100);

            return bundle;
        }

        private NodeContextBundle BuildCompactSearchContextBundle(NodeControl current)
        {
            var bundle = CreateBaseContextBundle(current);

            var upstream = CollectUpstream(current, 12);
            var downstream = CollectDownstream(current, 3);

            bundle.UpstreamContext = BuildContextSection(
                upstream,
                topLimit: 700,
                bottomLimit: 500,
                maxCount: int.MaxValue);

            bundle.DownstreamContext = BuildContextSection(
                downstream,
                topLimit: 320,
                bottomLimit: 240,
                maxCount: int.MaxValue);

            bundle.BranchSummaryContext = BuildBranchSummaryContext(
                current,
                upstream,
                downstream,
                representativeCountPerBranch: 2,
                summaryTopLimit: 70,
                summaryBottomLimit: 60);

            return bundle;
        }

        private NodeContextBundle BuildResearchContextBundle(NodeControl current)
        {
            var bundle = CreateBaseContextBundle(current);

            var upstream = CollectUpstream(current, 20);
            var downstream = CollectDownstream(current, 6);

            bundle.UpstreamContext = BuildContextSection(
                upstream,
                topLimit: 1100,
                bottomLimit: 1000,
                maxCount: int.MaxValue);

            bundle.DownstreamContext = BuildContextSection(
                downstream,
                topLimit: 520,
                bottomLimit: 420,
                maxCount: int.MaxValue);

            bundle.BranchSummaryContext = BuildBranchSummaryContext(
                current,
                upstream,
                downstream,
                representativeCountPerBranch: 4,
                summaryTopLimit: 120,
                summaryBottomLimit: 110);

            return bundle;
        }

        private NodeContextBundle CreateBaseContextBundle(NodeControl current)
        {
            var bundle = new NodeContextBundle();

            var atts = _main.GetAttachmentsForNode(current);
            if (atts.Count > 0)
            {
                bundle.AttachmentHint =
                    "\n\n【本節點附件】\n" +
                    string.Join("\n", atts.Select(a => $"- ({a.Kind}) {a.FileName}"));
            }

            return bundle;
        }

        private string BuildBranchSummaryContext(
            NodeControl current,
            IEnumerable<NodeControl> upstream,
            IEnumerable<NodeControl> downstream,
            int representativeCountPerBranch,
            int summaryTopLimit,
            int summaryBottomLimit)
        {
            var excluded = new HashSet<Guid> { current.Id };
            foreach (var n in upstream) excluded.Add(n.Id);
            foreach (var n in downstream) excluded.Add(n.Id);

            var allOthers = _main.GetAllNodesInCanvas()
                .Where(n => !excluded.Contains(n.Id))
                .ToList();

            if (allOthers.Count == 0)
                return "";

            var visited = new HashSet<Guid>();
            var branchGroups = new List<List<NodeControl>>();

            foreach (var node in allOthers)
            {
                if (!visited.Add(node.Id))
                    continue;

                var group = CollectUndirectedConnectedGroup(node, excluded);
                foreach (var g in group)
                    visited.Add(g.Id);

                if (group.Count > 0)
                    branchGroups.Add(group);
            }

            if (branchGroups.Count == 0)
                return "";

            var lines = new List<string>
            {
                $"（以下為其它支線摘要，共 {branchGroups.Count} 條。僅供理解全局，不可蓋過目前節點與主鏈。）"
            };

            int branchIndex = 1;
            foreach (var group in branchGroups.OrderByDescending(g => g.Count))
            {
                var representatives = group
                    .OrderByDescending(ScoreNodeForSummary)
                    .Take(Math.Max(1, representativeCountPerBranch))
                    .ToList();

                var summaryParts = new List<string>();
                foreach (var n in representatives)
                {
                    var top = Truncate((n.GetTopText() ?? "").Trim(), summaryTopLimit);
                    var bottom = Truncate((n.GetBottomText() ?? "").Trim(), summaryBottomLimit);

                    if (!string.IsNullOrWhiteSpace(top) && !string.IsNullOrWhiteSpace(bottom))
                        summaryParts.Add($"Top: {top} / Bottom: {bottom}");
                    else if (!string.IsNullOrWhiteSpace(top))
                        summaryParts.Add($"Top: {top}");
                    else if (!string.IsNullOrWhiteSpace(bottom))
                        summaryParts.Add($"Bottom: {bottom}");
                }

                if (summaryParts.Count == 0)
                    continue;

                lines.Add($"- 支線 {branchIndex}（{group.Count} 節點）");
                foreach (var part in summaryParts)
                    lines.Add($"  • {part}");

                branchIndex++;
            }

            return string.Join("\n", lines);
        }

        private List<NodeControl> CollectUndirectedConnectedGroup(NodeControl seed, HashSet<Guid> excluded)
        {
            var result = new List<NodeControl>();
            var queue = new Queue<NodeControl>();
            var visited = new HashSet<Guid>();

            queue.Enqueue(seed);
            visited.Add(seed.Id);

            while (queue.Count > 0)
            {
                var current = queue.Dequeue();
                result.Add(current);

                foreach (var next in GetUndirectedNeighbors(current))
                {
                    if (next == null) continue;
                    if (excluded.Contains(next.Id)) continue;
                    if (!visited.Add(next.Id)) continue;

                    queue.Enqueue(next);
                }
            }

            return result;
        }

        private IEnumerable<NodeControl> GetUndirectedNeighbors(NodeControl node)
        {
            foreach (var c in _main.GetConnectionsForContext())
            {
                if (ReferenceEquals(c.StartNode, node) && c.EndNode != null)
                    yield return c.EndNode;

                if (ReferenceEquals(c.EndNode, node) && c.StartNode != null)
                    yield return c.StartNode;
            }
        }

        private static int ScoreNodeForSummary(NodeControl n)
        {
            int score = 0;
            var top = (n.GetTopText() ?? "").Trim();
            var bottom = (n.GetBottomText() ?? "").Trim();

            score += Math.Min(top.Length, 200);
            score += Math.Min(bottom.Length, 120);

            if (n.GetTopLocked())
                score += 30;

            return score;
        }

        private static string BuildContextSection(
            IEnumerable<NodeControl> nodes,
            int topLimit,
            int bottomLimit,
            int maxCount)
        {
            var source = nodes ?? Enumerable.Empty<NodeControl>();
            if (maxCount != int.MaxValue)
                source = source.Take(maxCount);

            var list = source
                .Select(n =>
                {
                    var top = Truncate((n.GetTopText() ?? "").Trim(), topLimit);
                    var bottom = Truncate((n.GetBottomText() ?? "").Trim(), bottomLimit);

                    if (string.IsNullOrWhiteSpace(top) && string.IsNullOrWhiteSpace(bottom))
                        return "";

                    if (!string.IsNullOrWhiteSpace(top) && !string.IsNullOrWhiteSpace(bottom))
                        return $"Top: {top}\nBottom: {bottom}";

                    if (!string.IsNullOrWhiteSpace(top))
                        return $"Top: {top}";

                    return $"Bottom: {bottom}";
                })
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            return list.Count == 0 ? "" : string.Join("\n\n", list);
        }

        private List<NodeControl> CollectUpstream(NodeControl current, int maxDepth)
        {
            var result = new List<NodeControl>();
            var cursor = current;
            int depth = 0;

            while (depth < maxDepth)
            {
                var incoming = _main.GetConnectionsForContext()
                    .FirstOrDefault(c => ReferenceEquals(c.EndNode, cursor));

                if (incoming?.StartNode == null)
                    break;

                cursor = incoming.StartNode;
                result.Insert(0, cursor);
                depth++;
            }

            return result;
        }

        private List<NodeControl> CollectDownstream(NodeControl current, int maxDepth)
        {
            var result = new List<NodeControl>();
            var cursor = current;
            int depth = 0;

            while (depth < maxDepth)
            {
                var outgoing = _main.GetConnectionsForContext()
                    .FirstOrDefault(c => ReferenceEquals(c.StartNode, cursor));

                if (outgoing?.EndNode == null)
                    break;

                cursor = outgoing.EndNode;
                result.Add(cursor);
                depth++;
            }

            return result;
        }

        private static string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (maxChars <= 0) return "";
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "…";
        }
    }
}