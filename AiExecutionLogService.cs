using System;
using System.Collections.Generic;
using System.Linq;

namespace test
{
    public sealed class AiExecutionLogService
    {
        private readonly Dictionary<string, List<AiExecutionLogEntry>> _logsByNodeId = new();

        public void Add(AiExecutionLogEntry entry)
        {
            if (entry == null || string.IsNullOrWhiteSpace(entry.NodeId))
                return;

            if (!_logsByNodeId.TryGetValue(entry.NodeId, out var list))
            {
                list = new List<AiExecutionLogEntry>();
                _logsByNodeId[entry.NodeId] = list;
            }

            list.Add(entry);

            // 先保留最近 20 筆，避免一直長大
            if (list.Count > 20)
            {
                int removeCount = list.Count - 20;
                list.RemoveRange(0, removeCount);
            }
        }

        public IReadOnlyList<AiExecutionLogEntry> GetLogs(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                return Array.Empty<AiExecutionLogEntry>();

            if (_logsByNodeId.TryGetValue(nodeId, out var list))
                return list.ToList();

            return Array.Empty<AiExecutionLogEntry>();
        }

        public AiExecutionLogEntry? GetLatest(string nodeId)
        {
            return GetLogs(nodeId).LastOrDefault();
        }

        public void ClearNode(string nodeId)
        {
            if (string.IsNullOrWhiteSpace(nodeId))
                return;

            _logsByNodeId.Remove(nodeId);
        }

        public void ClearAll()
        {
            _logsByNodeId.Clear();
        }
    }
}