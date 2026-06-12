using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using PathShape = System.Windows.Shapes.Path;

namespace test
{
    public partial class MainWindow : Window
    {
        private double _scale = 1.0;
        private ScaleTransform _scaleTransform = null!;
        private TranslateTransform _translateTransform = null!;
        private bool _isSidebarCollapsed = false;
        private int _zIndexCounter = 0;

        private NodeControl? _initialNode;

        private bool _fileNameLockedByUser = false;

        private DispatcherTimer? _autoRenameTimer;
        private CancellationTokenSource? _autoRenameCts;
        private string _lastInitialTopSnapshot = "";
        private string _lastAppliedAutoKeyword = "";

        private readonly AiServiceRouter _aiRouter = new();
        private NodeService _nodeService = null!;
        public NodeService NodeService => _nodeService;

        private NodeControl? _hoveredDecisionNode;
        private NodeControl? _lastDecisionNode;
        private const double DecisionPanelCompactWidth = 340;
        private const double DecisionPanelCompactHeight = 260;
        private const double DecisionPanelExpandedWidth = 720;
        private const double DecisionPanelExpandedHeight = 760;
        private const string DecisionPanelExpandIconData = "M2,6 L2,2 L6,2 M8,2 L12,2 L12,6 M12,8 L12,12 L8,12 M6,12 L2,12 L2,8";
        private const string DecisionPanelCollapseIconData = "M2,2 L6,6 M6,2 L6,6 L2,6 M12,12 L8,8 M8,12 L8,8 L12,8";
        private bool _isDecisionPanelExpanded = false;

        private readonly NodeModelSelectionService _nodeModelSelection = new();
        private readonly HashSet<string> _expandedDecisionStepKeys = new();
        private readonly Dictionary<Guid, NodeDecisionViewData> _liveDecisionViewsByNode = new();

        private readonly Dictionary<Guid, string> _autoFlowTemplatesByNode = new();
        private readonly Dictionary<Guid, NodeAutoFlowPolicy> _autoFlowPoliciesByNode = new();
        private readonly HashSet<Guid> _unsupportedDownstreamNodeIds = new();

        public enum EditReason
        {
            None = 0,
            UserEdit = 1,
            NewNode = 2
        }

        private NodeControl? _editingNode;
        private EditReason _editingReason = EditReason.None;

        private class Connection
        {
            public PathShape Path = null!;
            public NodeControl StartNode = null!;
            public string StartThumb = "ThumbTL";
            public NodeControl EndNode = null!;
            public string EndThumb = "ThumbTR";
        }

        public sealed class ContextConnectionInfo
        {
            public NodeControl StartNode { get; init; } = null!;
            public string StartThumb { get; init; } = "ThumbTL";
            public NodeControl EndNode { get; init; } = null!;
            public string EndThumb { get; init; } = "ThumbTR";
        }

        public IReadOnlyList<ContextConnectionInfo> GetConnectionsForContext()
        {
            return _connections
                .Where(c => c.StartNode != null && c.EndNode != null)
                .Select(c => new ContextConnectionInfo
                {
                    StartNode = c.StartNode,
                    StartThumb = c.StartThumb,
                    EndNode = c.EndNode,
                    EndThumb = c.EndThumb
                })
                .ToList();
        }

        private readonly List<Connection> _connections = new();
        public int GetNextZIndex() => ++_zIndexCounter;

        private bool _isPanning = false;
        private Point _lastMousePos;

        private static readonly Random _random = new();

        private string SavesDir => @"D:\desk\college\final\file";
        private string AttachmentsRootDir => System.IO.Path.Combine(SavesDir, "_attachments");
        private string GeneratedFilesDir => System.IO.Path.Combine(SavesDir, "_generated");
        internal string GetGeneratedFilesDir() => GeneratedFilesDir;

        private string? _currentFilePath;
        private bool _hasStarted = false;
        private bool _suppressSave = false;

        private readonly Dictionary<Guid, List<AttachmentInfo>> _attachmentsByNode = new();
        private readonly Dictionary<Guid, string> _nodeAgentsById = new();
        private readonly Dictionary<Guid, string> _nodeModelsById = new();
        private readonly Dictionary<Guid, NodeTaskMode> _nodeTaskModesById = new();

        private bool _isAutoModelSelectionEnabled = false;
        private bool _isAdvancedAutoResolverEnabled = false;

        private readonly AiExecutionLogService _executionLogService = new();

        private void ApplyActiveTimelineVisual(
    Border cardBorder,
    Border dotOuter,
    Border dotInner,
    NodeDecisionStepState state)
        {
            if (cardBorder == null || dotOuter == null || dotInner == null)
                return;

            var pulseBrush = GetStepBrush(state);

            cardBorder.BorderThickness = new Thickness(1.6);

            var shadow = cardBorder.Effect as DropShadowEffect;
            if (shadow == null)
            {
                shadow = new DropShadowEffect
                {
                    BlurRadius = 16,
                    ShadowDepth = 0,
                    Opacity = 0.18,
                    Color = Colors.Black
                };
                cardBorder.Effect = shadow;
            }

            shadow.Color = pulseBrush.Color;
            shadow.BlurRadius = 22;
            shadow.Opacity = 0.22;

            var borderAnim = new ThicknessAnimation
            {
                From = new Thickness(1.6),
                To = new Thickness(2.4),
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            cardBorder.BeginAnimation(Border.BorderThicknessProperty, borderAnim);

            var shadowBlurAnim = new DoubleAnimation
            {
                From = 18,
                To = 28,
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, shadowBlurAnim);

            var shadowOpacityAnim = new DoubleAnimation
            {
                From = 0.14,
                To = 0.28,
                Duration = TimeSpan.FromMilliseconds(900),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            shadow.BeginAnimation(DropShadowEffect.OpacityProperty, shadowOpacityAnim);

            var dotScale = new ScaleTransform(1.0, 1.0);
            dotOuter.RenderTransformOrigin = new Point(0.5, 0.5);
            dotOuter.RenderTransform = dotScale;

            var dotScaleAnim = new DoubleAnimation
            {
                From = 1.0,
                To = 1.18,
                Duration = TimeSpan.FromMilliseconds(700),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            dotScale.BeginAnimation(ScaleTransform.ScaleXProperty, dotScaleAnim);
            dotScale.BeginAnimation(ScaleTransform.ScaleYProperty, dotScaleAnim);

            var innerOpacityAnim = new DoubleAnimation
            {
                From = 0.65,
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(700),
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever
            };
            dotInner.BeginAnimation(UIElement.OpacityProperty, innerOpacityAnim);
        }

        public bool NodeAcceptsAutoFlowInput(NodeControl node)
        {
            if (node == null)
                return false;

            string template = GetAutoFlowTemplate(node);
            if (string.IsNullOrWhiteSpace(template))
                return false;

            return template.Contains("{{input}}", StringComparison.Ordinal);
        }

        private void ClearTimelineAnimations(Border cardBorder, Border dotOuter, Border dotInner)
        {
            if (cardBorder != null)
            {
                cardBorder.BeginAnimation(Border.BorderThicknessProperty, null);

                if (cardBorder.Effect is DropShadowEffect shadow)
                {
                    shadow.BeginAnimation(DropShadowEffect.BlurRadiusProperty, null);
                    shadow.BeginAnimation(DropShadowEffect.OpacityProperty, null);
                }
            }

            if (dotOuter?.RenderTransform is ScaleTransform scale)
            {
                scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                scale.ScaleX = 1.0;
                scale.ScaleY = 1.0;
            }

            if (dotInner != null)
            {
                dotInner.BeginAnimation(UIElement.OpacityProperty, null);
                dotInner.Opacity = 1.0;
            }
        }
        public sealed class AttachmentInfo
        {
            public string FileName { get; set; } = "";
            public string RelativePath { get; set; } = "";
            public string MimeType { get; set; } = "application/octet-stream";
            public string Kind { get; set; } = "file";
        }

        private sealed class FileItem
        {
            public string FullPath { get; }
            public string DisplayName { get; }

            public FileItem(string fullPath)
            {
                FullPath = fullPath;
                DisplayName = System.IO.Path.GetFileNameWithoutExtension(fullPath);
            }
        }

        private record NodeState(
    string Id,
    double X,
    double Y,
    double Width,
    double Height,
    string? TopText,
    string? BottomText,
    bool TopLocked,
    double FontSize,
    string? AgentId = null,
    string? NodeModel = null,
    string? TaskMode = null,
    bool UnsupportedDownstreamNode = false
);

        private record ConnState(string StartId, string EndId, string StartThumb, string EndThumb);

        private record AttachmentState(
    string NodeId,
    string FileName,
    string RelativePath,
    string MimeType,
    string Kind
);



        private record ExecutionLogState(
            string NodeId,
            DateTime StartedAtUtc,
            DateTime EndedAtUtc,
            long DurationMs,

            string SelectionMode,
            string Resolver,
            string WorkspaceSummary,
            List<string> WorkspaceArtifactDetails,
            List<AgentWorkspaceArtifactRecord>? WorkspaceArtifacts,
            string RequestedModelId,
            string PlannedModelId,
            string ActualModelId,

            string TaskMode,
            double Confidence,

            string ResolverReason,
            List<string> ResolverKeywords,

            bool CapabilityAdjusted,
            string CapabilityReason,

            string CapabilityRequestedModelId,
            string CapabilityResolvedModelId,
            string CapabilityRequired,
            string CapabilityMissing,
            bool CapabilityStreamingAdjusted,

            List<AgentCapabilityTraceItem> CapabilityTrace,

            string RequestedAgentId,
            string ActualAgentId,

            bool RuntimeFallbackUsed,
            string RuntimeFallbackSummary,

            bool Success,
            string ErrorMessage,

            List<AiFallbackAttempt> FallbackAttempts
        );
        private record AppState(
            DateTime CreatedAt,
            string? InitialNodeId,
            List<NodeState> Nodes,
            List<ConnState> Connections,
            List<AttachmentState> Attachments,
            List<ExecutionLogState>? ExecutionLogs = null,
            bool FileNameLocked = false,
            bool AutoModelSelectionEnabled = false,
            bool AdvancedAutoResolverEnabled = false
        );

        private static string DisplayNameFromPath(string path)
            => System.IO.Path.GetFileNameWithoutExtension(path);

        public MainWindow()
        {
            InitializeComponent();

            Directory.CreateDirectory(SavesDir);
            Directory.CreateDirectory(AttachmentsRootDir);

            var tg = new TransformGroup();
            _scaleTransform = new ScaleTransform(1.0, 1.0);
            _translateTransform = new TranslateTransform(0.0, 0.0);
            tg.Children.Add(_scaleTransform);
            tg.Children.Add(_translateTransform);
            MainCanvas.RenderTransform = tg;

            Viewport.MouseDown += Viewport_MouseDown;
            Viewport.MouseUp += Viewport_MouseUp;
            Viewport.MouseMove += Viewport_MouseMove;

            Loaded += MainWindow_Loaded;
        }

        private static string BuildResolverKeywordSummary(AiExecutionLogEntry log)
        {
            if (log == null || log.ResolverKeywords == null || log.ResolverKeywords.Count == 0)
                return "";

            var keywords = log.ResolverKeywords
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (keywords.Count == 0)
                return "";

            return "keywords: " + string.Join(", ", keywords);
        }

        private static string BuildFallbackTraceSummary(AiExecutionLogEntry log)
        {
            if (log == null || log.FallbackAttempts == null || log.FallbackAttempts.Count == 0)
                return "";

            var parts = new List<string>();

            foreach (var attempt in log.FallbackAttempts)
            {
                if (attempt == null || string.IsNullOrWhiteSpace(attempt.ModelId))
                    continue;

                string modelLabel = AiModelHelper.GetDefinition(attempt.ModelId).DisplayName;
                string symbol = attempt.Success ? "?" : "?";

                parts.Add($"{attempt.AttemptIndex}.{modelLabel}{symbol}");
            }

            if (parts.Count == 0)
                return "";

            return "trace: " + string.Join(" → ", parts);
        }

        private static string BuildCapabilityDetailSummary(AiExecutionLogEntry log)
        {
            if (log == null || !log.CapabilityAdjusted)
                return "";

            string requestedLabel = AiModelHelper.GetDefinition(log.CapabilityRequestedModelId).DisplayName;
            string resolvedLabel = AiModelHelper.GetDefinition(log.CapabilityResolvedModelId).DisplayName;

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(log.CapabilityMissing))
                parts.Add($"missing: {log.CapabilityMissing}");

            if (!string.IsNullOrWhiteSpace(log.CapabilityRequired) &&
                !string.Equals(log.CapabilityRequired, AiModelCapability.None.ToString(), StringComparison.OrdinalIgnoreCase))
            {
                parts.Add($"required: {log.CapabilityRequired}");
            }

            if (!string.Equals(requestedLabel, resolvedLabel, StringComparison.OrdinalIgnoreCase))
                parts.Add($"{requestedLabel} → {resolvedLabel}");

            if (log.CapabilityStreamingAdjusted)
                parts.Add("streaming → off");

            if (parts.Count == 0)
                return "";

            return "capability: " + string.Join(" / ", parts);
        }

        private static ExecutionLogState ToExecutionLogState(AiExecutionLogEntry entry)
        {
            return new ExecutionLogState(
                NodeId: entry.NodeId ?? "",
                StartedAtUtc: entry.StartedAtUtc,
                EndedAtUtc: entry.EndedAtUtc,
                DurationMs: entry.DurationMs,

                SelectionMode: entry.SelectionMode ?? "",
                Resolver: entry.Resolver ?? "",
                WorkspaceSummary: entry.WorkspaceSummary ?? "",
                WorkspaceArtifactDetails: entry.WorkspaceArtifactDetails?.ToList() ?? new List<string>(),
                WorkspaceArtifacts: entry.WorkspaceArtifacts?.ToList() ?? new List<AgentWorkspaceArtifactRecord>(),

                RequestedModelId: entry.RequestedModelId ?? "",
                PlannedModelId: entry.PlannedModelId ?? "",
                ActualModelId: entry.ActualModelId ?? "",

                TaskMode: entry.TaskMode ?? "",
                Confidence: entry.Confidence,

                ResolverReason: entry.ResolverReason ?? "",
                ResolverKeywords: entry.ResolverKeywords?.ToList() ?? new List<string>(),

                CapabilityAdjusted: entry.CapabilityAdjusted,
                CapabilityReason: entry.CapabilityReason ?? "",

                CapabilityRequestedModelId: entry.CapabilityRequestedModelId ?? "",
                CapabilityResolvedModelId: entry.CapabilityResolvedModelId ?? "",
                CapabilityRequired: entry.CapabilityRequired ?? "",
                CapabilityMissing: entry.CapabilityMissing ?? "",
                CapabilityStreamingAdjusted: entry.CapabilityStreamingAdjusted,

                CapabilityTrace: entry.CapabilityTrace?.ToList() ?? new List<AgentCapabilityTraceItem>(),

                RequestedAgentId: entry.RequestedAgentId ?? "",
                ActualAgentId: entry.ActualAgentId ?? "",

                RuntimeFallbackUsed: entry.RuntimeFallbackUsed,
                RuntimeFallbackSummary: entry.RuntimeFallbackSummary ?? "",
                Success: entry.Success,
                ErrorMessage: entry.ErrorMessage ?? "",

                FallbackAttempts: entry.FallbackAttempts?.ToList() ?? new List<AiFallbackAttempt>()
            );
        }
        private static AiExecutionLogEntry ToExecutionLogEntry(ExecutionLogState state)
        {
            return new AiExecutionLogEntry
            {
                NodeId = state.NodeId ?? "",
                StartedAtUtc = state.StartedAtUtc,
                EndedAtUtc = state.EndedAtUtc,
                DurationMs = state.DurationMs,

                SelectionMode = state.SelectionMode ?? "",
                Resolver = state.Resolver ?? "",

                RequestedModelId = AiModelHelper.NormalizeNodeModel(state.RequestedModelId),
                PlannedModelId = AiModelHelper.NormalizeNodeModel(state.PlannedModelId),
                ActualModelId = AiModelHelper.NormalizeNodeModel(state.ActualModelId),

                TaskMode = state.TaskMode ?? "",
                Confidence = state.Confidence,

                ResolverReason = state.ResolverReason ?? "",
                ResolverKeywords = state.ResolverKeywords?.ToList() ?? new List<string>(),

                CapabilityAdjusted = state.CapabilityAdjusted,
                CapabilityReason = state.CapabilityReason ?? "",

                CapabilityRequestedModelId = AiModelHelper.NormalizeNodeModel(state.CapabilityRequestedModelId),
                CapabilityResolvedModelId = AiModelHelper.NormalizeNodeModel(state.CapabilityResolvedModelId),
                CapabilityRequired = state.CapabilityRequired ?? "",
                CapabilityMissing = state.CapabilityMissing ?? "",
                CapabilityStreamingAdjusted = state.CapabilityStreamingAdjusted,

                CapabilityTrace = state.CapabilityTrace?.ToList() ?? new List<AgentCapabilityTraceItem>(),

                RequestedAgentId = state.RequestedAgentId ?? "",
                ActualAgentId = state.ActualAgentId ?? "",

                RuntimeFallbackUsed = state.RuntimeFallbackUsed,
                RuntimeFallbackSummary = state.RuntimeFallbackSummary ?? "",
                WorkspaceSummary = state.WorkspaceSummary ?? "",
                WorkspaceArtifactDetails = state.WorkspaceArtifactDetails?.ToList() ?? new List<string>(),
                WorkspaceArtifacts = state.WorkspaceArtifacts?.ToList() ?? new List<AgentWorkspaceArtifactRecord>(),
                Success = state.Success,
                ErrorMessage = state.ErrorMessage ?? "",

                FallbackAttempts = state.FallbackAttempts?.ToList() ?? new List<AiFallbackAttempt>()
            };
        }

        private void DecisionPanelToggleButton_Click(object sender, RoutedEventArgs e)
        {
            _isDecisionPanelExpanded = !_isDecisionPanelExpanded;
            ApplyDecisionPanelSize();
        }

        private void ApplyDecisionPanelSize()
        {
            if (DecisionPanel != null)
            {
                DecisionPanel.Width = _isDecisionPanelExpanded
                    ? DecisionPanelExpandedWidth
                    : DecisionPanelCompactWidth;
                DecisionPanel.Height = _isDecisionPanelExpanded
                    ? DecisionPanelExpandedHeight
                    : DecisionPanelCompactHeight;
            }

            if (DecisionPanelToggleIcon != null)
                DecisionPanelToggleIcon.Data = Geometry.Parse(_isDecisionPanelExpanded
                    ? DecisionPanelCollapseIconData
                    : DecisionPanelExpandIconData);

            if (DecisionPanelToggleButton != null)
                DecisionPanelToggleButton.ToolTip = _isDecisionPanelExpanded ? "縮回決策窗" : "放大決策窗";
        }

        private void ShowDecisionForNode(NodeControl node)
        {
            if (node == null)
                return;

            _lastDecisionNode = node;

            if (_liveDecisionViewsByNode.TryGetValue(node.Id, out var liveView))
            {
                ApplyDecisionViewData(liveView);
                return;
            }

            var latest = GetLatestExecutionLog(node);
            if (latest != null)
            {
                var viewData = NodeDecisionViewBuilder.BuildFromLog(latest);
                ApplyDecisionViewData(viewData);
                return;
            }

            // 沒有 execution log 時，顯示即時預估資訊
            NodeTaskModeResolution previewResolution = NodeTaskModeResolver.Resolve(node.GetTopText() ?? "");
            NodeTaskMode previewTask = NodeTaskModeHelper.Normalize(previewResolution.Mode);
            string previewTaskName = NodeTaskModeHelper.ToDisplayName(previewTask);

            string previewAgent = GetEffectiveNodeAgent(node, node.GetTopText());
            string requestedModel = GetNodeSelectedModel(node);
            string effectiveModel = GetEffectiveNodeModel(node, node.GetTopText());
            string requestedLabel2 = AiModelHelper.GetDefinition(requestedModel).DisplayName;
            string effectiveLabel2 = AiModelHelper.GetDefinition(effectiveModel).DisplayName;

            string previewModel =
                string.Equals(requestedLabel2, effectiveLabel2, StringComparison.OrdinalIgnoreCase)
                    ? effectiveLabel2
                    : $"{effectiveLabel2} ← {requestedLabel2}";

            string modeText;
            string statusText;
            string resolverText;

            if (!IsAutoModelSelectionEnabled())
            {
                modeText = "Manual";
                statusText = "Manual";
                resolverText = "Manual";
            }
            else if (IsAdvancedAutoResolverEnabled())
            {
                modeText = "Auto";
                statusText = "API Auto";
                resolverText = "Responses API（預估）";
            }
            else
            {
                modeText = "Auto";
                statusText = "Rule Auto";
                resolverText = "Rules（預估）";
            }

            string previewSummary = $"{previewTaskName} / 尚未執行";

            string previewReason = string.IsNullOrWhiteSpace(previewResolution.Reason)
                ? "-"
                : previewResolution.Reason;

            string previewKeywords = "-";
            if (previewResolution.MatchedKeywords != null && previewResolution.MatchedKeywords.Count > 0)
            {
                previewKeywords = "keywords: " + string.Join(
                    ", ",
                    previewResolution.MatchedKeywords.Distinct(StringComparer.OrdinalIgnoreCase));
            }

            var previewViewData = new NodeDecisionViewData
            {
                Status = statusText,
                Mode = modeText,
                Resolver = resolverText,
                Agent = previewAgent,
                Model = previewModel,
                TaskSummary = previewSummary,
                Reason = previewReason,
                Keywords = previewKeywords,
                Extra = "-",
                CapabilitySummary = "-",
                CapabilityDetails = new List<string>(),
                DelegationSummary = "-",
                DelegationDetails = new List<string>(),
                CapabilityAdjusted = false,
                RuntimeFallbackUsed = false,
                ApiFallbackUsed = false,
                Steps = new[]
    {
        new NodeDecisionStepViewData
        {
            Title = "Task Mode",
            Detail = $"{previewTaskName} / 預估",
            State = NodeDecisionStepState.Info,
            Highlight = true
        },
        new NodeDecisionStepViewData
        {
            Title = "Model Selection",
            Detail = previewModel,
            State = NodeDecisionStepState.Info,
            Highlight = true
        },
        new NodeDecisionStepViewData
        {
            Title = "Capability",
            Detail = "尚未觸發",
            State = NodeDecisionStepState.Info,
            DetailLines = new[]
            {
                "Preview 階段尚未實際執行 capability"
            }
        },
        new NodeDecisionStepViewData
        {
            Title = "Delegation",
            Detail = "尚未觸發",
            State = NodeDecisionStepState.Info,
            DetailLines = new[]
            {
                "Preview 階段尚未實際執行 agent delegation"
            }
        },
        new NodeDecisionStepViewData
        {
            Title = "Execution",
            Detail = "尚未執行",
            State = NodeDecisionStepState.Info
        }
    }
            };

            ApplyDecisionViewData(previewViewData);
        }
        public void NotifyNodeHoverEntered(NodeControl node)
        {
            if (node == null)
                return;

            _hoveredDecisionNode = node;
            ShowDecisionForNode(node);
        }

        public void NotifyNodeHoverLeft(NodeControl node)
        {
            if (node == null)
                return;

            if (ReferenceEquals(_hoveredDecisionNode, node))
                _hoveredDecisionNode = null;

            // 這版先保留最後顯示內容，不自動重置
        }

        private static double SafeFinite(double value, double fallback = 0)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
                return fallback;

            return value;
        }

        private static double SafePositiveFinite(double value, double fallback)
        {
            if (double.IsNaN(value) || double.IsInfinity(value) || value <= 0)
                return fallback;

            return value;
        }

        private static string Truncate(string s, int maxChars)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.Length <= maxChars) return s;
            return s.Substring(0, maxChars) + "…";
        }

        public PerplexityService GetPerplexityToolService()
            => _aiRouter.GetPerplexityToolService();

        private string GetDefaultNodeModelId()
        {
            return AiModelRegistry.Default.Id;
        }

        private string GetDefaultAgentId()
        {
            return AgentRegistry.Default.Id;
        }

        private string NormalizeOrDefaultAgentId(string? agentId)
        {
            return AgentRegistry.IsKnown(agentId)
                ? AgentRegistry.Get(agentId).Id
                : GetDefaultAgentId();
        }

        public string GetNodeSelectedAgent(NodeControl node)
        {
            if (node == null)
                return GetDefaultAgentId();

            if (_nodeAgentsById.TryGetValue(node.Id, out var agentId))
                return NormalizeOrDefaultAgentId(agentId);

            var fallback = GetDefaultAgentId();
            _nodeAgentsById[node.Id] = fallback;
            return fallback;
        }

        public string GetEffectiveNodeAgent(NodeControl node, string? topText = null)
        {
            if (node == null)
                return GetDefaultAgentId();

            string selectedAgentId = GetNodeSelectedAgent(node);

            if (!_isAutoModelSelectionEnabled)
                return selectedAgentId;

            string text = topText ?? node.GetTopText() ?? "";
            var taskResolution = NodeTaskModeResolver.Resolve(text);
            var taskMode = NodeTaskModeHelper.Normalize(taskResolution.Mode);

            var resolver = new AgentSelectionResolver();
            var selection = resolver.Resolve(
                text,
                taskMode,
                GetAttachmentsForNode(node),
                selectedAgentId);

            return AgentRegistry.Get(selection.AgentId).Id;
        }

        public AgentDefinition GetNodeAgentDefinition(NodeControl node)
        {
            return AgentRegistry.Get(GetNodeSelectedAgent(node));
        }

        public void SetNodeSelectedAgent(NodeControl node, string agentId)
        {
            if (node == null)
                return;

            string normalized = NormalizeOrDefaultAgentId(agentId);
            _nodeAgentsById[node.Id] = normalized;

            var agent = AgentRegistry.Get(normalized);

            // Phase 1 相容模式：agent 改變時，同步預設 model/task
            _nodeModelsById[node.Id] = AiModelHelper.NormalizeNodeModel(agent.DefaultModelId);
            _nodeTaskModesById[node.Id] = NodeTaskModeHelper.Normalize(agent.DefaultTaskMode);

            SaveState();
        }

        private string NormalizeOrDefaultNodeModel(string? model)
        {
            return _aiRouter.NormalizeNodeModel(string.IsNullOrWhiteSpace(model)
                ? GetDefaultNodeModelId()
                : model);
        }

        private static NodeTaskMode GetDefaultNodeTaskMode()
        {
            return NodeTaskModeHelper.Default;
        }

        private static NodeTaskMode NormalizeOrDefaultTaskMode(NodeTaskMode mode)
        {
            return NodeTaskModeHelper.Normalize(mode);
        }

        private static NodeTaskMode ParseNodeTaskMode(string? raw)
        {
            return NodeTaskModeHelper.ParseOrDefault(raw);
        }

        public IReadOnlyList<NodeTaskModeOption> GetAllNodeTaskModes()
        {
            return NodeTaskModeHelper.All;
        }

        public NodeTaskMode GetNodeTaskMode(NodeControl node)
        {
            if (node == null)
                return GetDefaultNodeTaskMode();

            if (_nodeTaskModesById.TryGetValue(node.Id, out var mode))
                return NormalizeOrDefaultTaskMode(mode);

            var fallback = GetDefaultNodeTaskMode();
            _nodeTaskModesById[node.Id] = fallback;
            return fallback;
        }

        public void SetNodeTaskMode(NodeControl node, NodeTaskMode mode)
        {
            if (node == null) return;

            _nodeTaskModesById[node.Id] = NormalizeOrDefaultTaskMode(mode);
            SaveState();
        }

        public string GetNodeTaskModeStorageValue(NodeControl node)
        {
            return NodeTaskModeHelper.ToStorageValue(GetNodeTaskMode(node));
        }

        public string GetNodeTaskModeDisplayName(NodeControl node)
        {
            return NodeTaskModeHelper.ToDisplayName(GetNodeTaskMode(node));
        }

        private NodeTaskMode ResolvePreviewTaskModeForNode(NodeControl node)
        {
            if (node == null)
                return GetDefaultNodeTaskMode();

            string text = node.GetTopText() ?? "";
            if (string.IsNullOrWhiteSpace(text))
                return GetNodeTaskMode(node);

            var resolution = NodeTaskModeResolver.Resolve(text);
            return NodeTaskModeHelper.Normalize(resolution.Mode);
        }

        public string GetNodeSelectedModel(NodeControl node)
        {
            if (node == null)
                return GetDefaultNodeModelId();

            if (_nodeModelsById.TryGetValue(node.Id, out var model))
                return NormalizeOrDefaultNodeModel(model);

            var fallback = GetDefaultNodeModelId();
            _nodeModelsById[node.Id] = fallback;
            return fallback;
        }

        public void AddExecutionLog(AiExecutionLogEntry entry)
        {
            _executionLogService.Add(entry);
        }

        public IReadOnlyList<AiExecutionLogEntry> GetExecutionLogs(NodeControl node)
        {
            if (node == null)
                return Array.Empty<AiExecutionLogEntry>();

            return _executionLogService.GetLogs(node.Id.ToString());
        }

        public AiExecutionLogEntry? GetLatestExecutionLog(NodeControl node)
        {
            if (node == null)
                return null;

            return _executionLogService.GetLatest(node.Id.ToString());
        }

        public void RefreshDecisionForNode(NodeControl node)
        {
            if (node == null)
                return;

            ShowDecisionForNode(node);
        }

        public void ClearExecutionLogs(NodeControl node)
        {
            if (node == null)
                return;

            _executionLogService.ClearNode(node.Id.ToString());
        }

        public void SetNodeSelectedModel(NodeControl node, string model)
        {
            if (node == null) return;

            _nodeModelsById[node.Id] = NormalizeOrDefaultNodeModel(model);
            SaveState();
        }

        public bool IsAutoModelSelectionEnabled()
        {
            return _isAutoModelSelectionEnabled;
        }

        public bool IsAdvancedAutoResolverEnabled()
        {
            return _isAdvancedAutoResolverEnabled;
        }

        public void SetAutoModelSelectionEnabled(bool enabled, bool save = true)
        {
            _isAutoModelSelectionEnabled = enabled;

            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = enabled;

            if (!enabled)
                _isAdvancedAutoResolverEnabled = false;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();
            RefreshAllNodeModelSelectionUIs();

            if (save)
                SaveState();
        }

        public void SetAdvancedAutoResolverEnabled(bool enabled, bool save = true)
        {
            if (!_isAutoModelSelectionEnabled)
                enabled = false;

            _isAdvancedAutoResolverEnabled = enabled;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = enabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();
            RefreshAllNodeModelSelectionUIs();

            if (save)
                SaveState();
        }

        private void UpdateAdvancedAutoResolverVisibility()
        {
            if (AdvancedAutoResolverSwitch == null)
                return;

            AdvancedAutoResolverSwitch.Visibility =
                _isAutoModelSelectionEnabled
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }

        private void UpdateDecisionPanelForCurrentMode()
        {
            if (!_isAutoModelSelectionEnabled)
            {
                SetDecisionVisualization(
    status: "Manual",
    mode: "Manual",
    resolver: "Manual",
    model: "-",
    taskSummary: "-",
    reason: "-",
    keywords: "-",
    extra: "-",
    statusBrushHex: "#EDEDED",
    statusTextBrushHex: "#404040");
                return;
            }

            if (_isAdvancedAutoResolverEnabled)
            {
                SetDecisionVisualization(
     status: "API Auto",
     mode: "Auto",
     resolver: "Responses API",
     model: "等待送出",
     taskSummary: "-",
     reason: "-",
     keywords: "-",
     extra: "-",
     statusBrushHex: "#EAF4FF",
     statusTextBrushHex: "#245A9B");
                return;
            }

            SetDecisionVisualization(
     status: "Rule Auto",
     mode: "Auto",
     resolver: "Rules",
     model: "等待送出",
     taskSummary: "-",
     reason: "-",
     keywords: "-",
     extra: "-",
     statusBrushHex: "#EEF7EA",
     statusTextBrushHex: "#2E6A2E");
        }

        public void SetDecisionVisualization(
    string status,
    string mode,
    string resolver,
    string model,
    string taskSummary,
    string reason = "-",
    string keywords = "-",
    string extra = "-",
    string agent = "-",
    string statusBrushHex = "#EDEDED",
    string statusTextBrushHex = "#404040")
        {
            Dispatcher.Invoke(() =>
            {
                if (DecisionStatusText != null)
                    DecisionStatusText.Text = string.IsNullOrWhiteSpace(status) ? "-" : status;

                if (DecisionModeText != null)
                    DecisionModeText.Text = string.IsNullOrWhiteSpace(mode) ? "-" : mode;

                if (DecisionResolverText != null)
                    DecisionResolverText.Text = string.IsNullOrWhiteSpace(resolver) ? "-" : resolver;

                if (DecisionAgentText != null)
                    DecisionAgentText.Text = string.IsNullOrWhiteSpace(agent) ? "-" : agent;

                if (DecisionModelText != null)
                    DecisionModelText.Text = string.IsNullOrWhiteSpace(model) ? "-" : model;

                if (DecisionTaskText != null)
                    DecisionTaskText.Text = string.IsNullOrWhiteSpace(taskSummary) ? "-" : taskSummary;

                if (DecisionReasonText != null)
                    DecisionReasonText.Text = string.IsNullOrWhiteSpace(reason) ? "-" : reason;

                if (DecisionKeywordsText != null)
                    DecisionKeywordsText.Text = string.IsNullOrWhiteSpace(keywords) ? "-" : keywords;

                if (DecisionExtraText != null)
                    DecisionExtraText.Text = string.IsNullOrWhiteSpace(extra) ? "-" : extra;

                if (DecisionStatusBadge != null)
                    DecisionStatusBadge.Background = CreateBrush(statusBrushHex, "#EDEDED");

                if (DecisionStatusText != null)
                    DecisionStatusText.Foreground = CreateBrush(statusTextBrushHex, "#404040");
            });
        }
        private static SolidColorBrush CreateBrush(string? hex, string fallbackHex)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(hex))
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
            }
            catch { }

            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(fallbackHex)!);
        }

        private void ApplyDecisionViewData(NodeDecisionViewData viewData)
        {
            if (viewData == null)
                return;

            string extra = viewData.Extra;

            if (!string.IsNullOrWhiteSpace(viewData.DelegationSummary) &&
                !string.Equals(viewData.DelegationSummary, "-", StringComparison.Ordinal))
            {
                if (string.IsNullOrWhiteSpace(extra) || extra == "-")
                    extra = "delegation: " + viewData.DelegationSummary;
                else
                    extra += " / delegation: " + viewData.DelegationSummary;
            }

            ApplyDecisionThemeByMode(
                status: viewData.Status,
                mode: viewData.Mode,
                resolver: viewData.Resolver,
                agent: viewData.Agent,
                model: viewData.Model,
                taskSummary: viewData.TaskSummary,
                reason: viewData.Reason,
                keywords: viewData.Keywords,
                extra: extra,
                capabilityAdjusted: viewData.CapabilityAdjusted,
                runtimeFallbackUsed: viewData.RuntimeFallbackUsed,
                apiFallbackUsed: viewData.ApiFallbackUsed);

            RenderDecisionTimeline(viewData.Steps);
        }

        private void RenderDecisionTimeline(IReadOnlyList<NodeDecisionStepViewData> steps)
        {
            Dispatcher.Invoke(() =>
            {
                if (DecisionTimelineHost == null)
                    return;

                DecisionTimelineHost.Children.Clear();

                if (steps == null || steps.Count == 0)
                {
                    var emptyBorder = new Border
                    {
                        Background = CreateBrush("#FAFAFA", "#FAFAFA"),
                        BorderBrush = CreateBrush("#E9E9E9", "#E9E9E9"),
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(12),
                        Padding = new Thickness(12),
                        Child = new TextBlock
                        {
                            Text = "尚無 Decision Timeline",
                            FontSize = 12,
                            Foreground = CreateBrush("#7A7A7A", "#7A7A7A")
                        }
                    };

                    DecisionTimelineHost.Children.Add(emptyBorder);
                    return;
                }

                for (int i = 0; i < steps.Count; i++)
                {
                    var item = CreateDecisionTimelineItem(
                        step: steps[i],
                        index: i,
                        isLast: i == steps.Count - 1,
                        isFirst: i == 0);

                    DecisionTimelineHost.Children.Add(item);
                }
            });
        }

        private FrameworkElement CreateDecisionTimelineItem(
    NodeDecisionStepViewData step,
    int index,
    bool isLast,
    bool isFirst)
        {
            string safeTitle = step?.Title ?? "";
            string safeDetail = step?.Detail ?? "";
            var safeState = step?.State ?? NodeDecisionStepState.Info;
            bool safeHighlight = step?.Highlight == true;
            bool safeIsActive = step?.IsActive == true;
            bool safeIsExpandable = step?.IsExpandable == true;
            var safeDetailLines = step?.DetailLines ?? Array.Empty<string>();
            string stepKey = $"{index}:{safeTitle}:{safeDetail}";
            bool isExpanded = _expandedDecisionStepKeys.Contains(stepKey);

            var root = new Grid
            {
                Margin = new Thickness(0, 0, 0, isLast ? 0 : 12)
            };

            root.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(12) });
            root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            // ===== 左側 timeline 區 =====
            var timelineGrid = new Grid
            {
                Width = 20
            };
            timelineGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            timelineGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

            if (!isFirst)
            {
                var topLine = new Border
                {
                    Width = 2,
                    Height = 10,
                    Background = CreateBrush("#D9DDE4", "#D9DDE4"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Top
                };
                Grid.SetRow(topLine, 0);
                timelineGrid.Children.Add(topLine);
            }

            var dotOuter = new Border
            {
                Width = 14,
                Height = 14,
                CornerRadius = new CornerRadius(999),
                Background = CreateBrush("#FFFFFF", "#FFFFFF"),
                BorderBrush = GetStepBrush(safeState),
                BorderThickness = new Thickness(2),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var dotInner = new Border
            {
                Width = 6,
                Height = 6,
                CornerRadius = new CornerRadius(999),
                Background = GetStepBrush(safeState),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };

            dotOuter.Child = dotInner;
            Grid.SetRow(dotOuter, 0);
            timelineGrid.Children.Add(dotOuter);

            if (!isLast)
            {
                var bottomLine = new Border
                {
                    Width = 2,
                    Background = CreateBrush("#D9DDE4", "#D9DDE4"),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 8, 0, 0)
                };
                Grid.SetRow(bottomLine, 1);
                timelineGrid.Children.Add(bottomLine);
            }

            Grid.SetColumn(timelineGrid, 0);
            root.Children.Add(timelineGrid);

            // ===== 右側卡片 =====
            var cardBorder = new Border
            {
                Background = safeHighlight
                    ? CreateBrush("#F6FAFF", "#F6FAFF")
                    : CreateBrush("#FFFFFF", "#FFFFFF"),
                BorderBrush = GetStepBorderBrush(safeState, safeHighlight || safeIsActive),
                BorderThickness = new Thickness(safeIsActive ? 1.6 : 1),
                CornerRadius = new CornerRadius(14),
                Padding = new Thickness(12, 10, 12, 10),
                Cursor = safeIsExpandable ? Cursors.Hand : Cursors.Arrow
            };

            var shadow = new DropShadowEffect
            {
                BlurRadius = safeIsActive ? 20 : (safeHighlight ? 14 : 10),
                ShadowDepth = 0,
                Opacity = safeIsActive ? 0.18 : (safeHighlight ? 0.14 : 0.08),
                Color = safeIsActive ? GetStepBrush(safeState).Color : Colors.Black
            };
            cardBorder.Effect = shadow;

            var contentPanel = new StackPanel();

            var headerGrid = new Grid();
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var titleStack = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            var indexBadge = new Border
            {
                Background = GetStepSoftBrush(safeState),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(7, 2, 7, 2),
                Margin = new Thickness(0, 0, 8, 0),
                Child = new TextBlock
                {
                    Text = (index + 1).ToString(),
                    FontSize = 11,
                    FontWeight = FontWeights.SemiBold,
                    Foreground = GetStepBrush(safeState),
                    VerticalAlignment = VerticalAlignment.Center
                }
            };

            var titleText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(safeTitle) ? "-" : safeTitle,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#232323", "#232323"),
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };

            titleStack.Children.Add(indexBadge);
            titleStack.Children.Add(titleText);

            Grid.SetColumn(titleStack, 0);
            headerGrid.Children.Add(titleStack);

            var stateBadge = new Border
            {
                Background = GetStepSoftBrush(safeState),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(8, 3, 8, 3),
                VerticalAlignment = VerticalAlignment.Center,
                Child = new TextBlock
                {
                    Text = safeIsActive ? "Running" : GetStepStateLabel(safeState),
                    FontSize = 11,
                    FontWeight = FontWeights.Medium,
                    Foreground = GetStepBrush(safeState)
                }
            };

            Grid.SetColumn(stateBadge, 1);
            headerGrid.Children.Add(stateBadge);

            contentPanel.Children.Add(headerGrid);

            var detailText = new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(safeDetail) ? "-" : safeDetail,
                FontSize = 12,
                Margin = new Thickness(0, 8, 0, 0),
                Foreground = CreateBrush("#5D5D5D", "#5D5D5D"),
                TextWrapping = TextWrapping.Wrap
            };
            contentPanel.Children.Add(detailText);

            if (safeIsExpandable)
            {
                var actionRow = new DockPanel
                {
                    Margin = new Thickness(0, 8, 0, 0),
                    LastChildFill = false
                };

                var expandHint = new TextBlock
                {
                    Text = isExpanded ? "收合詳細資訊 ▲" : "展開詳細資訊 ▼",
                    FontSize = 11.5,
                    Foreground = CreateBrush("#6F6F6F", "#6F6F6F"),
                    FontWeight = FontWeights.Medium
                };

                DockPanel.SetDock(expandHint, Dock.Right);
                actionRow.Children.Add(expandHint);
                contentPanel.Children.Add(actionRow);
            }

            if (isExpanded && safeDetailLines.Count > 0)
            {
                var detailHost = new StackPanel
                {
                    Margin = new Thickness(0, 10, 0, 0)
                };

                var separator = new Border
                {
                    Height = 1,
                    Background = CreateBrush("#ECEFF4", "#ECEFF4"),
                    Margin = new Thickness(0, 0, 0, 10)
                };
                detailHost.Children.Add(separator);

                if (IsWorkspaceStep(step))
                {
                    detailHost.Children.Add(CreateWorkspaceInspector(safeDetailLines));
                }
                else
                {
                    foreach (var line in safeDetailLines)
                    {
                        detailHost.Children.Add(new Border
                        {
                            Background = CreateBrush("#FAFBFD", "#FAFBFD"),
                            BorderBrush = CreateBrush("#EEF1F4", "#EEF1F4"),
                            BorderThickness = new Thickness(1),
                            CornerRadius = new CornerRadius(8),
                            Padding = new Thickness(8, 6, 8, 6),
                            Margin = new Thickness(0, 0, 0, 6),
                            Child = new TextBlock
                            {
                                Text = string.IsNullOrWhiteSpace(line) ? "-" : line,
                                FontSize = 11.5,
                                Foreground = CreateBrush("#666666", "#666666"),
                                TextWrapping = TextWrapping.Wrap
                            }
                        });
                    }
                }

                contentPanel.Children.Add(detailHost);
            }

            cardBorder.Child = contentPanel;

            AttachCopyContextMenu(
                cardBorder,
                "複製此決策區塊",
                () => BuildDecisionStepCopyText(step, index));

            if (safeIsExpandable)
            {
                cardBorder.MouseLeftButtonUp += (_, __) =>
                {
                    if (_expandedDecisionStepKeys.Contains(stepKey))
                        _expandedDecisionStepKeys.Remove(stepKey);
                    else
                        _expandedDecisionStepKeys.Add(stepKey);

                    var target = _lastDecisionNode ?? _hoveredDecisionNode;
                    if (target != null)
                        ShowDecisionForNode(target);
                };

                cardBorder.MouseEnter += (_, __) =>
                {
                    if (!safeIsActive)
                    {
                        cardBorder.Background = safeHighlight
                            ? CreateBrush("#F0F7FF", "#F0F7FF")
                            : CreateBrush("#FAFAFA", "#FAFAFA");
                    }
                };

                cardBorder.MouseLeave += (_, __) =>
                {
                    if (!safeIsActive)
                    {
                        cardBorder.Background = safeHighlight
                            ? CreateBrush("#F6FAFF", "#F6FAFF")
                            : CreateBrush("#FFFFFF", "#FFFFFF");
                    }
                };
            }

            if (safeIsActive)
            {
                ApplyActiveTimelineVisual(cardBorder, dotOuter, dotInner, safeState);

                cardBorder.Background = safeHighlight
                    ? CreateBrush("#EEF6FF", "#EEF6FF")
                    : CreateBrush("#F8FBFF", "#F8FBFF");
            }
            else
            {
                ClearTimelineAnimations(cardBorder, dotOuter, dotInner);
            }

            Grid.SetColumn(cardBorder, 2);
            root.Children.Add(cardBorder);

            return root;
        }

        private static bool IsWorkspaceStep(NodeDecisionStepViewData? step)
        {
            return string.Equals(step?.Title, "Workspace", StringComparison.OrdinalIgnoreCase);
        }

        private static string BuildDecisionStepCopyText(
            NodeDecisionStepViewData? step,
            int index)
        {
            if (step == null)
                return "";

            var lines = new List<string>
            {
                $"{index + 1}. {SafeCopy(step.Title)}",
                $"State: {step.State}" + (step.IsActive ? " / Running" : ""),
                $"Detail: {SafeCopy(step.Detail)}"
            };

            if (step.DetailLines != null && step.DetailLines.Count > 0)
            {
                lines.Add("");
                lines.Add("Details:");
                lines.AddRange(step.DetailLines.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            return string.Join(Environment.NewLine, lines);
        }

        private static string SafeCopy(string? text)
            => string.IsNullOrWhiteSpace(text) ? "-" : text.Trim();

        private FrameworkElement CreateWorkspaceInspector(IReadOnlyList<string> lines)
        {
            var root = new StackPanel();

            if (lines == null || lines.Count == 0)
            {
                root.Children.Add(CreateWorkspaceTextCard("-", muted: true));
                return root;
            }

            var currentArtifactFacts = new StackPanel
            {
                Margin = new Thickness(0, 8, 0, 0)
            };

            Border? currentArtifactCard = null;
            List<string>? currentArtifactLines = null;

            foreach (var raw in lines)
            {
                string trimmed = (raw ?? "").Trim();

                if (string.IsNullOrWhiteSpace(trimmed))
                    continue;

                if (trimmed.Equals("--- Artifacts ---", StringComparison.OrdinalIgnoreCase))
                {
                    root.Children.Add(CreateWorkspaceSectionLabel("Artifacts"));
                    continue;
                }

                if (trimmed.StartsWith("Artifact:", StringComparison.OrdinalIgnoreCase))
                {
                    var artifactLines = new List<string> { trimmed };
                    currentArtifactLines = artifactLines;

                    currentArtifactFacts = new StackPanel
                    {
                        Margin = new Thickness(0, 8, 0, 0)
                    };

                    currentArtifactCard = CreateArtifactCard(
                        trimmed,
                        currentArtifactFacts,
                        () => string.Join(Environment.NewLine, artifactLines),
                        trimmed.Contains("Type: downstream_node_plan", StringComparison.OrdinalIgnoreCase)
                            ? () => TryMaterializeDownstreamNodePlanFromText(
                                string.Join(Environment.NewLine, artifactLines))
                            : null);

                    root.Children.Add(currentArtifactCard);
                    continue;
                }

                if (trimmed.StartsWith("VerifiedFacts:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateWorkspaceTextCard(trimmed, muted: true));
                    continue;
                }

                if (trimmed.StartsWith("Fact:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateFactCard(trimmed));
                    continue;
                }

                if (trimmed.StartsWith("Snapshot:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("SnapshotFile:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("SnapshotPreview:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateCodeSnapshotCard(trimmed));
                    continue;
                }

                if (trimmed.StartsWith("Diff:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("DiffBase:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("DiffFile:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("UnifiedDiff:", StringComparison.OrdinalIgnoreCase) ||
                    trimmed.StartsWith("DiffNote:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateDiffTextCard(trimmed));
                    continue;
                }

                if (trimmed.StartsWith("OwnerAgent:", StringComparison.OrdinalIgnoreCase))
                {
                    currentArtifactLines?.Add(trimmed);
                    var target = currentArtifactCard == null ? root : currentArtifactFacts;
                    target.Children.Add(CreateOwnershipTags(trimmed));
                    continue;
                }

                currentArtifactLines?.Add(trimmed);
                root.Children.Add(CreateWorkspaceTextCard(trimmed, muted: true));
            }

            return root;
        }

        private FrameworkElement CreateWorkspaceSectionLabel(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#57606A", "#57606A"),
                Margin = new Thickness(0, 2, 0, 8)
            };
        }

        private Border CreateArtifactCard(
            string line,
            StackPanel factsHost,
            Func<string> copyTextProvider,
            Action? applyDownstreamPlan = null)
        {
            var parts = line.Substring("Artifact:".Length).Trim()
                .Split('/')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            string kind = parts.Count > 0 ? parts[0] : "artifact";
            string format = parts.Count > 1 ? parts[1] : "text";
            string visibility = parts.Count > 2 ? parts[2] : "visible";
            string meta = parts.Count > 3 ? string.Join(" / ", parts.Skip(3)) : "";

            var panel = new StackPanel();

            var header = new WrapPanel();
            header.Children.Add(CreateWorkspaceBadge(kind, "#EAF4FF", "#245A9B"));
            header.Children.Add(CreateWorkspaceBadge(format, "#F2F4F7", "#475467"));
            header.Children.Add(CreateWorkspaceBadge(visibility, "#F7F7F7", "#666666"));
            panel.Children.Add(header);

            if (!string.IsNullOrWhiteSpace(meta))
            {
                panel.Children.Add(new TextBlock
                {
                    Text = meta,
                    FontSize = 11.5,
                    Foreground = CreateBrush("#4A4A4A", "#4A4A4A"),
                    Margin = new Thickness(0, 6, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });
            }

            panel.Children.Add(factsHost);

            var card = new Border
            {
                Background = CreateBrush("#FFFFFF", "#FFFFFF"),
                BorderBrush = CreateBrush("#DDE7F2", "#DDE7F2"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8),
                Child = panel
            };

            AttachCopyContextMenu(
                card,
                "複製此主題區塊",
                copyTextProvider,
                applyDownstreamPlan);

            return card;
        }

        private Border CreateCodeSnapshotCard(string line)
        {
            string label = "Snapshot";
            string body = line;

            int colon = line.IndexOf(':');
            if (colon >= 0)
            {
                label = line.Substring(0, colon).Trim();
                body = line.Substring(colon + 1).Trim();
            }

            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#3B4A66", "#3B4A66"),
                TextWrapping = TextWrapping.Wrap
            });

            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(body) ? "-" : body,
                FontSize = 11.5,
                Foreground = CreateBrush("#43536C", "#43536C"),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            return new Border
            {
                Background = CreateBrush("#F4F7FC", "#F4F7FC"),
                BorderBrush = CreateBrush("#D7E1F0", "#D7E1F0"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = panel
            };
        }

        private Border CreateDiffTextCard(string line)
        {
            string label = "Diff";
            string body = line;

            int colon = line.IndexOf(':');
            if (colon >= 0)
            {
                label = line.Substring(0, colon).Trim();
                body = line.Substring(colon + 1).Trim();
            }

            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#2F5F52", "#2F5F52"),
                TextWrapping = TextWrapping.Wrap
            });

            panel.Children.Add(new TextBlock
            {
                Text = string.IsNullOrWhiteSpace(body) ? "-" : body,
                FontSize = 11.5,
                Foreground = CreateBrush("#365950", "#365950"),
                Margin = new Thickness(0, 3, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });

            return new Border
            {
                Background = CreateBrush("#F0FAF6", "#F0FAF6"),
                BorderBrush = CreateBrush("#BFE5D7", "#BFE5D7"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = panel
            };
        }

        private Border CreateFactCard(string line)
        {
            string content = line.Substring("Fact:".Length).Trim();
            string subject = content;
            string value = "";

            int equalsIndex = content.IndexOf('=');
            if (equalsIndex >= 0)
            {
                subject = content.Substring(0, equalsIndex).Trim();
                value = content.Substring(equalsIndex + 1).Trim();
            }

            var panel = new StackPanel();

            panel.Children.Add(new TextBlock
            {
                Text = subject,
                FontSize = 11.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = CreateBrush("#252525", "#252525"),
                TextWrapping = TextWrapping.Wrap
            });

            if (!string.IsNullOrWhiteSpace(value))
            {
                var valueParts = value
                    .Split(new[] { " / " }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(x => x.Trim())
                    .Where(x => !string.IsNullOrWhiteSpace(x))
                    .ToList();

                if (valueParts.Count == 0)
                    valueParts.Add(value);

                panel.Children.Add(new TextBlock
                {
                    Text = valueParts[0],
                    FontSize = 12,
                    Foreground = CreateBrush("#245A9B", "#245A9B"),
                    Margin = new Thickness(0, 4, 0, 0),
                    TextWrapping = TextWrapping.Wrap
                });

                foreach (var meta in valueParts.Skip(1))
                {
                    panel.Children.Add(new TextBlock
                    {
                        Text = meta,
                        FontSize = 11,
                        Foreground = CreateBrush("#51606F", "#51606F"),
                        Margin = new Thickness(0, 2, 0, 0),
                        TextWrapping = TextWrapping.Wrap
                    });
                }
            }

            return new Border
            {
                Background = CreateBrush("#F8FBFF", "#F8FBFF"),
                BorderBrush = CreateBrush("#D8E8F8", "#D8E8F8"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 7, 8, 7),
                Margin = new Thickness(0, 0, 0, 6),
                Child = panel
            };
        }

        private FrameworkElement CreateOwnershipTags(string line)
        {
            var wrap = new WrapPanel
            {
                Margin = new Thickness(0, 0, 0, 6)
            };

            var parts = line
                .Split('|')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            foreach (var part in parts)
            {
                bool numeric = part.Contains("numeric_fact_source", StringComparison.OrdinalIgnoreCase);
                bool official = part.Contains("official", StringComparison.OrdinalIgnoreCase);
                bool background = part.Contains("background_context", StringComparison.OrdinalIgnoreCase);

                string bg = numeric ? "#EAF7EF" : official ? "#EEF4FF" : background ? "#FFF7E8" : "#F2F4F7";
                string fg = numeric ? "#1F7A3A" : official ? "#245A9B" : background ? "#9A5A00" : "#475467";

                wrap.Children.Add(CreateWorkspaceBadge(part, bg, fg));
            }

            return wrap;
        }

        private Border CreateWorkspaceBadge(string text, string bgHex, string fgHex)
        {
            return new Border
            {
                Background = CreateBrush(bgHex, bgHex),
                CornerRadius = new CornerRadius(999),
                Padding = new Thickness(7, 3, 7, 3),
                Margin = new Thickness(0, 0, 5, 5),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(text) ? "-" : text,
                    FontSize = 10.5,
                    FontWeight = FontWeights.Medium,
                    Foreground = CreateBrush(fgHex, fgHex),
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private Border CreateWorkspaceTextCard(string text, bool muted)
        {
            return new Border
            {
                Background = CreateBrush(muted ? "#FAFBFD" : "#FFFFFF", "#FAFBFD"),
                BorderBrush = CreateBrush("#EEF1F4", "#EEF1F4"),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(8, 6, 8, 6),
                Margin = new Thickness(0, 0, 0, 6),
                Child = new TextBlock
                {
                    Text = string.IsNullOrWhiteSpace(text) ? "-" : text,
                    FontSize = 11.5,
                    Foreground = CreateBrush(muted ? "#666666" : "#333333", "#666666"),
                    TextWrapping = TextWrapping.Wrap
                }
            };
        }

        private void AttachCopyContextMenu(
            FrameworkElement target,
            string menuText,
            Func<string> copyTextProvider,
            Action? applyDownstreamPlan = null)
        {
            if (target == null || copyTextProvider == null)
                return;

            var item = new MenuItem
            {
                Header = string.IsNullOrWhiteSpace(menuText) ? "複製" : menuText,
                Style = TryFindResource("FileMenuItemStyle") as Style
            };

            item.Click += (_, __) =>
            {
                string text = copyTextProvider() ?? "";
                if (string.IsNullOrWhiteSpace(text))
                    return;

                try
                {
                    Clipboard.SetText(text);
                }
                catch
                {
                    // Clipboard may be temporarily locked by another process.
                }
            };

            var menu = new ContextMenu
            {
                Style = TryFindResource("FileContextMenuStyle") as Style
            };

            menu.Items.Add(item);

            if (applyDownstreamPlan != null)
            {
                var sepStyle = TryFindResource("FileSeparatorStyle") as Style;
                var applyItem = new MenuItem
                {
                    Header = "套用到畫布",
                    Style = TryFindResource("FileMenuItemStyle") as Style
                };

                applyItem.Click += (_, __) => applyDownstreamPlan();

                menu.Items.Add(new Separator { Style = sepStyle });
                menu.Items.Add(applyItem);
            }

            target.ContextMenu = menu;
        }

        private SolidColorBrush GetStepBrush(NodeDecisionStepState state)
        {
            return state switch
            {
                NodeDecisionStepState.Success => CreateBrush("#2E9B52", "#2E9B52"),
                NodeDecisionStepState.Warning => CreateBrush("#D48A00", "#D48A00"),
                NodeDecisionStepState.Error => CreateBrush("#C93C3C", "#C93C3C"),
                _ => CreateBrush("#4F7EF7", "#4F7EF7")
            };
        }

        private SolidColorBrush GetStepSoftBrush(NodeDecisionStepState state)
        {
            return state switch
            {
                NodeDecisionStepState.Success => CreateBrush("#EAF7EF", "#EAF7EF"),
                NodeDecisionStepState.Warning => CreateBrush("#FFF5E7", "#FFF5E7"),
                NodeDecisionStepState.Error => CreateBrush("#FDECEC", "#FDECEC"),
                _ => CreateBrush("#EDF3FF", "#EDF3FF")
            };
        }

        private SolidColorBrush GetStepBorderBrush(NodeDecisionStepState state, bool highlight)
        {
            if (highlight)
            {
                return state switch
                {
                    NodeDecisionStepState.Success => CreateBrush("#BFE3CB", "#BFE3CB"),
                    NodeDecisionStepState.Warning => CreateBrush("#F1D39B", "#F1D39B"),
                    NodeDecisionStepState.Error => CreateBrush("#E9B0B0", "#E9B0B0"),
                    _ => CreateBrush("#C9D9FF", "#C9D9FF")
                };
            }

            return state switch
            {
                NodeDecisionStepState.Success => CreateBrush("#D7EBDD", "#D7EBDD"),
                NodeDecisionStepState.Warning => CreateBrush("#F3E2BA", "#F3E2BA"),
                NodeDecisionStepState.Error => CreateBrush("#F0CCCC", "#F0CCCC"),
                _ => CreateBrush("#E8ECF3", "#E8ECF3")
            };
        }

        private static string GetStepStateLabel(NodeDecisionStepState state)
        {
            return state switch
            {
                NodeDecisionStepState.Success => "Success",
                NodeDecisionStepState.Warning => "Warning",
                NodeDecisionStepState.Error => "Error",
                _ => "Info"
            };
        }
        private void ApplyDecisionThemeByMode(
    string status,
    string mode,
    string resolver,
    string agent,
    string model,
    string taskSummary,
    string reason,
    string keywords,
    string extra,
    bool capabilityAdjusted,
    bool runtimeFallbackUsed,
    bool apiFallbackUsed)
        {
            if (runtimeFallbackUsed)
            {
                SetDecisionVisualization(
                    status: status,
                    mode: mode,
                    resolver: resolver,
                    model: model,
                    taskSummary: taskSummary,
                    reason: reason,
                    keywords: keywords,
                    extra: extra,
                    agent: agent,
                    statusBrushHex: "#FFE9E9",
                    statusTextBrushHex: "#9B2C2C");
                return;
            }

            if (capabilityAdjusted || apiFallbackUsed)
            {
                SetDecisionVisualization(
                    status: status,
                    mode: mode,
                    resolver: resolver,
                    model: model,
                    taskSummary: taskSummary,
                    reason: reason,
                    keywords: keywords,
                    extra: extra,
                    agent: agent,
                    statusBrushHex: "#FFF4E8",
                    statusTextBrushHex: "#9A5A00");
                return;
            }

            if (string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(status, "API Auto", StringComparison.OrdinalIgnoreCase))
            {
                SetDecisionVisualization(
                    status: status,
                    mode: mode,
                    resolver: resolver,
                    model: model,
                    taskSummary: taskSummary,
                    reason: reason,
                    keywords: keywords,
                    extra: extra,
                    agent: agent,
                    statusBrushHex: "#EAF4FF",
                    statusTextBrushHex: "#245A9B");
                return;
            }

            if (string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase))
            {
                SetDecisionVisualization(
                    status: status,
                    mode: mode,
                    resolver: resolver,
                    model: model,
                    taskSummary: taskSummary,
                    reason: reason,
                    keywords: keywords,
                    extra: extra,
                    agent: agent,
                    statusBrushHex: "#EEF7EA",
                    statusTextBrushHex: "#2E6A2E");
                return;
            }

            SetDecisionVisualization(
                status: status,
                mode: mode,
                resolver: resolver,
                model: model,
                taskSummary: taskSummary,
                reason: reason,
                keywords: keywords,
                extra: extra,
                agent: agent,
                statusBrushHex: "#EDEDED",
                statusTextBrushHex: "#404040");
        }

        public string GetEffectiveNodeModel(NodeControl node, string? topText = null)
        {
            var manualModel = GetNodeSelectedModel(node);

            if (!_isAutoModelSelectionEnabled)
                return manualModel;

            string text = topText ?? node?.GetTopText() ?? "";
            var resolution = NodeTaskModeResolver.Resolve(text);

            return _nodeModelSelection.ResolveRuleAutoModel(
                NodeTaskModeHelper.Normalize(resolution.Mode),
                manualModel);
        }

        public bool CanUserManuallySelectModel()
        {
            return !_isAutoModelSelectionEnabled;
        }

        public void RefreshAllNodeModelSelectionUIs()
        {
            foreach (var node in MainCanvas.Children.OfType<NodeControl>())
            {
                node.RefreshModelSelectionUI();
            }
        }

        public void RequestBeginEdit(NodeControl node, EditReason reason)
        {
            if (node == null) return;

            if (_editingNode != null && !ReferenceEquals(_editingNode, node))
            {
                _editingNode.ForceExitEditMode();
                _editingNode = null;
                _editingReason = EditReason.None;
            }

            _editingNode = node;
            _editingReason = reason;

            node.EnterEditMode();
        }

        public void NotifyEditEnded(NodeControl node)
        {
            if (node == null) return;

            if (_editingNode != null && ReferenceEquals(_editingNode, node))
            {
                _editingNode = null;
                _editingReason = EditReason.None;
            }
        }

        public void NotifyNodeSubmitted(NodeControl node)
        {
            if (node == null) return;
            if (!IsInitialNode(node)) return;

            ScheduleAutoRenameFromInitialNode(node);
        }

        private void ClearEditingIfDeleted(NodeControl node)
        {
            if (_editingNode != null && ReferenceEquals(_editingNode, node))
            {
                _editingNode = null;
                _editingReason = EditReason.None;
            }
        }

        private void MainWindow_Loaded(object sender, RoutedEventArgs e)
        {
            Sidebar.Visibility = Visibility.Visible;
            StartUI.Visibility = Visibility.Visible;
            MainUI.Visibility = Visibility.Collapsed;
            _isSidebarCollapsed = false;

            SetRandomStartMessage();
            RefreshFileList();

            _aiRouter.WarmupSafely();
            _nodeService = new NodeService(_aiRouter, this);

            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = _isAutoModelSelectionEnabled;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();
        }

        private void SetRandomStartMessage()
        {
            string[] messages =
            {
                "讓我們有個新的開始！",
                "或許我們該從頭來過…",
                "再來一次吧！忘掉過去的那些…",
                "來點新的？"
            };
            StartMessage.Text = messages[_random.Next(messages.Length)];
        }

        private void StartButton_Click(object sender, RoutedEventArgs e)
        {
            StartUI.Visibility = Visibility.Collapsed;
            MainUI.Visibility = Visibility.Visible;

            _currentFilePath = System.IO.Path.Combine(SavesDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
            CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";
            _hasStarted = true;

            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = _isAutoModelSelectionEnabled;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();

            _fileNameLockedByUser = false;
            _lastAppliedAutoKeyword = "";
            _lastInitialTopSnapshot = "";

            _attachmentsByNode.Clear();
            _nodeModelsById.Clear();
            _nodeTaskModesById.Clear();

            _editingNode = null;
            _editingReason = EditReason.None;

            Dispatcher.InvokeAsync(() =>
            {
                var node = new NodeControl();
                double x = MainCanvas.ActualWidth / 2 - node.Width / 2;
                double y = MainCanvas.ActualHeight / 2 - node.Height / 2;

                Canvas.SetLeft(node, SafeFinite(x, 0));
                Canvas.SetTop(node, SafeFinite(y, 0));
                Canvas.SetZIndex(node, GetNextZIndex());
                MainCanvas.Children.Add(node);
                HookNode(node);

                _nodeAgentsById[node.Id] = GetDefaultAgentId();

                var defaultAgent = AgentRegistry.Get(GetDefaultAgentId());
                _nodeModelsById[node.Id] = AiModelHelper.NormalizeNodeModel(defaultAgent.DefaultModelId);
                _nodeTaskModesById[node.Id] = NodeTaskModeHelper.Normalize(defaultAgent.DefaultTaskMode);
                _initialNode = node;

                _scale = 1.0;
                _scaleTransform.ScaleX = 1.0;
                _scaleTransform.ScaleY = 1.0;
                _translateTransform.X = 0.0;
                _translateTransform.Y = 0.0;

                RequestBeginEdit(node, EditReason.NewNode);

                SaveState();
                RefreshFileList();

            }, DispatcherPriority.Loaded);
        }

        private void NewFile_Click(object sender, RoutedEventArgs e)
        {
            StartUI.Visibility = Visibility.Visible;
            MainUI.Visibility = Visibility.Collapsed;
            SetRandomStartMessage();

            _hasStarted = false;
            _currentFilePath = null;
            CurrentFileLabel.Text = "目前檔案：尚未建立";
            ClearAll();

            _fileNameLockedByUser = false;
            _lastAppliedAutoKeyword = "";
            _lastInitialTopSnapshot = "";
            _attachmentsByNode.Clear();
            _nodeModelsById.Clear();
            _nodeTaskModesById.Clear();
            _nodeAgentsById.Clear();

            _editingNode = null;
            _editingReason = EditReason.None;

            _isAutoModelSelectionEnabled = false;
            _isAdvancedAutoResolverEnabled = false;

            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = false;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = false;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();
        }

        private void FileList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (FileList.SelectedItem is FileItem item && File.Exists(item.FullPath))
            {
                LoadState(item.FullPath);
            }
        }

        private void FileList_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (TrySelectItemUnderMouse(FileList, e.GetPosition(FileList), out var item) == false)
                return;

            if (item == null || !File.Exists(item.FullPath))
                return;

            var cmStyle = (Style)FindResource("FileContextMenuStyle");
            var miStyle = (Style)FindResource("FileMenuItemStyle");
            var sepStyle = (Style)FindResource("FileSeparatorStyle");

            var menu = new ContextMenu
            {
                Style = cmStyle,
                PlacementTarget = FileList
            };

            var miRename = new MenuItem { Header = "重新命名", Style = miStyle };
            miRename.Click += (_, __) => RenameFile(item.FullPath);

            var miDelete = new MenuItem { Header = "刪除", Style = miStyle };
            miDelete.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F")!);
            miDelete.Click += (_, __) => DeleteFile(item.FullPath);

            var sep = new Separator { Style = sepStyle };

            menu.Items.Add(miRename);
            menu.Items.Add(sep);
            menu.Items.Add(miDelete);

            menu.IsOpen = true;
            e.Handled = true;
        }

        private static bool TrySelectItemUnderMouse(ListBox listBox, Point pos, out FileItem? selectedItem)
        {
            selectedItem = null;

            var element = listBox.InputHitTest(pos) as DependencyObject;
            while (element != null && element is not ListBoxItem)
                element = VisualTreeHelper.GetParent(element);

            if (element is not ListBoxItem item)
                return false;

            listBox.SelectedItem = item.DataContext;
            selectedItem = listBox.SelectedItem as FileItem;
            return selectedItem != null;
        }

        private void DeleteFile(string path)
        {
            var name = DisplayNameFromPath(path);

            bool ok = MenuConfirmDialog.ShowDeleteConfirm(
                owner: this,
                title: "刪除確認",
                message: $"確定要刪除檔案？\n{name}",
                resourceHost: this);

            if (!ok) return;

            try
            {
                File.Delete(path);

                var folder = GetAttachmentFolderForFile(path);
                if (Directory.Exists(folder))
                {
                    try { Directory.Delete(folder, recursive: true); } catch { }
                }

                if (!string.IsNullOrEmpty(_currentFilePath) &&
                    string.Equals(_currentFilePath, path, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFilePath = null;
                    _hasStarted = false;
                    CurrentFileLabel.Text = "目前檔案：尚未建立";
                    _fileNameLockedByUser = false;
                    _lastAppliedAutoKeyword = "";
                    _lastInitialTopSnapshot = "";
                    _attachmentsByNode.Clear();
                    _nodeModelsById.Clear();
                    _nodeTaskModesById.Clear();
                    _isAutoModelSelectionEnabled = false;
                    _isAdvancedAutoResolverEnabled = false;

                    if (AutoModelSwitch != null)
                        AutoModelSwitch.IsChecked = false;

                    if (AdvancedAutoResolverSwitch != null)
                        AdvancedAutoResolverSwitch.IsChecked = false;

                    UpdateAdvancedAutoResolverVisibility();
                    UpdateDecisionPanelForCurrentMode();
                }

                RefreshFileList();
            }
            catch (Exception ex)
            {
                MessageBox.Show($"刪除失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private void RenameFile(string oldPath)
        {
            var oldName = DisplayNameFromPath(oldPath);

            var newName = SimpleInputDialog.Show(
                owner: this,
                title: "重新命名",
                prompt: "輸入新的檔名：",
                defaultValue: oldName);

            if (string.IsNullOrWhiteSpace(newName)) return;

            newName = NormalizeKeywordForFileName(newName);
            if (string.IsNullOrWhiteSpace(newName))
                return;

            var newPath = System.IO.Path.Combine(SavesDir, newName + ".json");

            if (string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                return;

            if (File.Exists(newPath))
            {
                MessageBox.Show("已存在同名檔案，請換一個名稱。", "重新命名失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            try
            {
                var oldBaseName = DisplayNameFromPath(oldPath);
                var newBaseName = DisplayNameFromPath(newPath);

                var oldFolder = GetAttachmentFolderForFile(oldPath);
                var newFolder = GetAttachmentFolderForFile(newPath);

                File.Move(oldPath, newPath);

                bool attachmentFolderHandled = MoveAttachmentFolderSafely(oldFolder, newFolder, out var folderMoveError);
                if (!attachmentFolderHandled)
                {
                    try
                    {
                        if (File.Exists(newPath) && !File.Exists(oldPath))
                            File.Move(newPath, oldPath);
                    }
                    catch { }

                    MessageBox.Show(
                        $"重新命名失敗：附件資料夾無法同步搬移。\n{folderMoveError}",
                        "錯誤",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                    return;
                }

                RewriteAttachmentRelativePathsOnDisk(newPath, oldBaseName, newBaseName);
                MarkFileNameLockedOnDisk(newPath);

                if (!string.IsNullOrEmpty(_currentFilePath) &&
                    string.Equals(_currentFilePath, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    _currentFilePath = newPath;
                    _fileNameLockedByUser = true;

                    UpdateAttachmentRelativePathsInMemory(oldBaseName, newBaseName);
                    RefreshAllNodeAttachmentUIs();

                    CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";

                    SaveState();
                }

                RefreshFileList();
                SelectFileInList(newPath);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"重新命名失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private bool MoveAttachmentFolderSafely(string oldFolder, string newFolder, out string errorMessage)
        {
            errorMessage = "";

            try
            {
                if (string.Equals(oldFolder, newFolder, StringComparison.OrdinalIgnoreCase))
                    return true;

                if (!Directory.Exists(oldFolder))
                    return true;

                if (!Directory.Exists(newFolder))
                {
                    Directory.Move(oldFolder, newFolder);
                    return true;
                }

                Directory.CreateDirectory(newFolder);

                foreach (var srcFile in Directory.GetFiles(oldFolder))
                {
                    var fileName = System.IO.Path.GetFileName(srcFile);
                    var destFile = System.IO.Path.Combine(newFolder, fileName);

                    if (File.Exists(destFile))
                    {
                        var uniqueName = $"{System.IO.Path.GetFileNameWithoutExtension(fileName)}_{Guid.NewGuid():N}{System.IO.Path.GetExtension(fileName)}";
                        destFile = System.IO.Path.Combine(newFolder, uniqueName);
                    }

                    File.Move(srcFile, destFile);
                }

                foreach (var srcDir in Directory.GetDirectories(oldFolder))
                {
                    var dirName = System.IO.Path.GetFileName(srcDir);
                    var destDir = System.IO.Path.Combine(newFolder, dirName);

                    if (Directory.Exists(destDir))
                    {
                        destDir = System.IO.Path.Combine(newFolder, $"{dirName}_{Guid.NewGuid():N}");
                    }

                    Directory.Move(srcDir, destDir);
                }

                if (!Directory.EnumerateFileSystemEntries(oldFolder).Any())
                {
                    Directory.Delete(oldFolder, recursive: false);
                }

                return true;
            }
            catch (Exception ex)
            {
                errorMessage = ex.Message;
                return false;
            }
        }

        private void RefreshAllNodeAttachmentUIs()
        {
            foreach (var node in MainCanvas.Children.OfType<NodeControl>())
            {
                node.RefreshAttachmentsUI();
            }
        }

        private void RefreshFileList()
        {
            var items = Directory.GetFiles(SavesDir, "*.json")
                                 .OrderByDescending(File.GetCreationTime)
                                 .Select(p => new FileItem(p))
                                 .ToList();

            FileList.ItemsSource = items;

            if (!string.IsNullOrEmpty(_currentFilePath))
                SelectFileInList(_currentFilePath);
        }

        private void SelectFileInList(string fullPath)
        {
            if (FileList.ItemsSource is not IEnumerable<FileItem> list) return;

            var target = list.FirstOrDefault(x => string.Equals(x.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
            if (target == null) return;

            FileList.SelectedItem = target;
            FileList.ScrollIntoView(target);
        }

        public void HookNode(NodeControl node)
        {
            if (!_nodeModelsById.ContainsKey(node.Id))
                _nodeModelsById[node.Id] = GetDefaultNodeModelId();

            if (!_nodeTaskModesById.ContainsKey(node.Id))
                _nodeTaskModesById[node.Id] = GetDefaultNodeTaskMode();

            if (!_autoFlowPoliciesByNode.ContainsKey(node.Id))
                _autoFlowPoliciesByNode[node.Id] = NodeAutoFlowPolicy.Default;
            node.Moved -= Node_Moved;
            node.Moved += Node_Moved;

            node.ContentChanged -= Node_ContentChanged;
            node.ContentChanged += Node_ContentChanged;
        }

        private void Node_Moved(object? sender, EventArgs e)
        {
            if (sender is NodeControl node)
                UpdateConnectionsFor(node);

            SaveState();
        }

        private void Node_ContentChanged(object? sender, EventArgs e)
        {
            SaveState();
        }

        private Point GetThumbCenterOnCanvas(NodeControl node, string thumbName)
        {
            if (node == null)
                return new Point(0, 0);

            return node.GetThumbCenterIgnoringHoverTransform(thumbName);
        }

        public void AddNode(double x, double y)
        {
            var node = new NodeControl();

            Canvas.SetLeft(node, SafeFinite(x - node.Width / 2, 0));
            Canvas.SetTop(node, SafeFinite(y - node.Height / 2, 0));
            Canvas.SetZIndex(node, GetNextZIndex());

            HookNode(node);

            _nodeAgentsById[node.Id] = GetDefaultAgentId();

            var defaultAgent = AgentRegistry.Get(GetDefaultAgentId());
            _nodeModelsById[node.Id] = AiModelHelper.NormalizeNodeModel(defaultAgent.DefaultModelId);
            _nodeTaskModesById[node.Id] = NodeTaskModeHelper.Normalize(defaultAgent.DefaultTaskMode);

            MainCanvas.Children.Add(node);

            Dispatcher.BeginInvoke(new Action(() =>
            {
                if (MainCanvas.Children.Contains(node))
                {
                    RequestBeginEdit(node, EditReason.NewNode);
                }
            }), DispatcherPriority.Loaded);

            SaveState();
        }

        public void CreateCurve(NodeControl startNode, string startThumbName, NodeControl endNode, string endThumbName)
        {
            var path = new PathShape
            {
                Stroke = (SolidColorBrush)new BrushConverter().ConvertFromString("#ADADAD")!,
                StrokeThickness = 18,
                IsHitTestVisible = false
            };

            var conn = new Connection
            {
                Path = path,
                StartNode = startNode,
                StartThumb = startThumbName,
                EndNode = endNode,
                EndThumb = endThumbName
            };

            UpdateConnectionGeometry(conn);
            Canvas.SetZIndex(path, GetNextZIndex());
            MainCanvas.Children.Add(path);
            _connections.Add(conn);

            HookNode(startNode);
            HookNode(endNode);

            SaveState();
        }

        public IReadOnlyList<NodeControl> MaterializeDownstreamNodePlan(
            NodeControl sourceNode,
            DownstreamNodePlanPayload plan)
        {
            if (sourceNode == null || plan?.ProposedNodes == null || plan.ProposedNodes.Count == 0)
                return Array.Empty<NodeControl>();

            var created = new List<NodeControl>();
            double sourceLeft = Canvas.GetLeft(sourceNode);
            double sourceTop = Canvas.GetTop(sourceNode);

            if (double.IsNaN(sourceLeft))
                sourceLeft = 0;

            if (double.IsNaN(sourceTop))
                sourceTop = 0;

            const double downstreamNodeSpacingX = 340;
            const double downstreamNodeOffsetY = 80;
            const double downstreamNodeRowGap = 360;
            double x = sourceLeft + sourceNode.Width + 130;
            double y = ResolveAvailableDownstreamRowY(
                sourceNode,
                x,
                sourceTop + downstreamNodeOffsetY,
                plan.ProposedNodes.Count,
                downstreamNodeSpacingX,
                downstreamNodeRowGap);
            NodeControl previousNode = sourceNode;
            int index = 0;

            foreach (var proposal in plan.ProposedNodes.Where(x => x != null))
            {
                var node = new NodeControl();
                Canvas.SetLeft(node, SafeFinite(x + index * downstreamNodeSpacingX, 0));
                Canvas.SetTop(node, SafeFinite(y, 0));
                Canvas.SetZIndex(node, GetNextZIndex());

                HookNode(node);
                MainCanvas.Children.Add(node);

                string requestedAgent = proposal.AgentId ?? "";
                string effectiveAgent = AgentRegistry.IsKnown(requestedAgent)
                    ? requestedAgent
                    : GetDefaultAgentId();
                bool requiresFutureAgent = !string.Equals(requestedAgent, effectiveAgent, StringComparison.OrdinalIgnoreCase);

                SetNodeSelectedAgent(node, effectiveAgent);

                var agent = AgentRegistry.Get(effectiveAgent);
                _nodeModelsById[node.Id] = AiModelHelper.NormalizeNodeModel(agent.DefaultModelId);
                _nodeTaskModesById[node.Id] = ResolveTaskModeForDownstreamProposal(proposal, agent);

                if (requiresFutureAgent)
                    _unsupportedDownstreamNodeIds.Add(node.Id);
                else
                    _unsupportedDownstreamNodeIds.Remove(node.Id);

                node.SetTopText(BuildDownstreamNodePrompt(proposal));
                node.SetBottomText("");
                node.SetTopLocked(false);
                SetNodeAutoFlowPolicy(node, new NodeAutoFlowPolicy
                {
                    AutoRunEnabled = false,
                    WaitForAllInputs = false,
                    StopFlowOnError = true,
                    AllowPartialInput = false
                });
                SyncAutoFlowTemplate(node, node.GetTopText());

                CreateCurve(previousNode, "ThumbTR", node, "ThumbTL");

                created.Add(node);
                previousNode = node;
                index++;
            }

            RefreshConnectionsAfterLayout(new[] { sourceNode }.Concat(created).ToList());
            SaveState();
            return created;
        }

        private double ResolveAvailableDownstreamRowY(
            NodeControl sourceNode,
            double startX,
            double preferredY,
            int nodeCount,
            double spacingX,
            double rowGap)
        {
            if (nodeCount <= 0)
                return preferredY;

            const double probeWidth = 260;
            const double probeHeight = 250;
            const double padding = 24;

            var existingRects = MainCanvas.Children
                .OfType<NodeControl>()
                .Where(x => !ReferenceEquals(x, sourceNode))
                .Select(GetNodeCanvasRect)
                .Where(x => x.Width > 0 && x.Height > 0)
                .ToList();

            for (int attempt = 0; attempt < 12; attempt++)
            {
                double y = preferredY + attempt * rowGap;
                bool overlaps = false;

                for (int i = 0; i < nodeCount; i++)
                {
                    var probe = new Rect(
                        startX + i * spacingX - padding,
                        y - padding,
                        probeWidth + padding * 2,
                        probeHeight + padding * 2);

                    if (existingRects.Any(existing => existing.IntersectsWith(probe)))
                    {
                        overlaps = true;
                        break;
                    }
                }

                if (!overlaps)
                    return y;
            }

            return preferredY + 12 * rowGap;
        }

        private static Rect GetNodeCanvasRect(NodeControl node)
        {
            if (node == null)
                return Rect.Empty;

            double left = Canvas.GetLeft(node);
            double top = Canvas.GetTop(node);

            if (double.IsNaN(left))
                left = 0;

            if (double.IsNaN(top))
                top = 0;

            double width = node.ActualWidth > 0
                ? node.ActualWidth
                : (!double.IsNaN(node.Width) && node.Width > 0 ? node.Width : 260);

            double height = node.ActualHeight > 0
                ? node.ActualHeight
                : (!double.IsNaN(node.Height) && node.Height > 0 ? node.Height : 250);

            return new Rect(left, top, width, height);
        }

        private void RefreshConnectionsAfterLayout(IReadOnlyList<NodeControl> nodes)
        {
            if (nodes == null || nodes.Count == 0)
                return;

            Dispatcher.BeginInvoke(new Action(() =>
            {
                MainCanvas.UpdateLayout();

                foreach (var node in nodes.Where(x => x != null && MainCanvas.Children.Contains(x)))
                    UpdateConnectionsFor(node);

                SaveState();
            }), DispatcherPriority.Loaded);
        }

        private void TryMaterializeDownstreamNodePlanFromText(string artifactText)
        {
            if (string.IsNullOrWhiteSpace(artifactText))
                return;

            var sourceNode = _lastDecisionNode;
            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
            {
                sourceNode = _hoveredDecisionNode;
            }

            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
            {
                sourceNode = _initialNode;
            }

            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
            {
                sourceNode = MainCanvas.Children.OfType<NodeControl>().FirstOrDefault();
            }

            if (sourceNode == null || !MainCanvas.Children.Contains(sourceNode))
                return;

            var plan = TryParseDownstreamNodePlan(artifactText, sourceNode.Id.ToString());
            if (plan == null || plan.ProposedNodes.Count == 0)
                return;

            var created = MaterializeDownstreamNodePlan(sourceNode, plan);
            if (created.Count > 0)
                FocusDecisionNode(created[0]);
        }

        private static double GetNodeLayoutHeight(NodeControl node)
        {
            if (node == null)
                return 0;

            if (node.ActualHeight > 0)
                return node.ActualHeight;

            if (node.Height > 0 && !double.IsNaN(node.Height))
                return node.Height;

            return 360;
        }

        private static DownstreamNodePlanPayload? TryParseDownstreamNodePlan(
            string text,
            string sourceNodeId)
        {
            var lines = (text ?? "")
                .Replace("\r\n", "\n")
                .Replace('\r', '\n')
                .Split('\n')
                .Select(x => x.Trim())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();

            if (!lines.Any(x => x.StartsWith("DownstreamPlan:", StringComparison.OrdinalIgnoreCase)))
                return null;

            string pipelineId = "";
            var taskType = OrchestrationTaskType.Workflow;
            var mutableNodes = new List<MutableDownstreamNodeProposal>();
            MutableDownstreamNodeProposal? currentNode = null;

            foreach (var line in lines)
            {
                if (line.StartsWith("DownstreamPlan:", StringComparison.OrdinalIgnoreCase))
                {
                    pipelineId = ExtractSlashField(line, "Pipeline");
                    string taskTypeText = ExtractSlashField(line, "TaskType");
                    if (Enum.TryParse(taskTypeText, true, out OrchestrationTaskType parsed))
                        taskType = parsed;

                    continue;
                }

                if (line.StartsWith("DownstreamNode:", StringComparison.OrdinalIgnoreCase))
                {
                    string body = line.Substring("DownstreamNode:".Length).Trim();
                    var parts = body
                        .Split('/')
                        .Select(x => x.Trim())
                        .ToList();

                    currentNode = new MutableDownstreamNodeProposal
                    {
                        Id = parts.Count > 0 ? parts[0] : "",
                        Label = parts.Count > 1 ? parts[1] : "",
                        AgentId = ExtractSlashField(line, "Agent"),
                        CapabilityId = ExtractSlashField(line, "Capability"),
                        Status = ExtractSlashField(line, "Status")
                    };

                    mutableNodes.Add(currentNode);
                    continue;
                }

                if (currentNode != null &&
                    line.StartsWith("InputSource:", StringComparison.OrdinalIgnoreCase))
                {
                    currentNode.InputSource = line.Substring("InputSource:".Length).Trim();
                    continue;
                }

                if (currentNode != null &&
                    line.StartsWith("ExpectedOutput:", StringComparison.OrdinalIgnoreCase))
                {
                    currentNode.ExpectedOutput = line.Substring("ExpectedOutput:".Length).Trim();
                }
            }

            var nodes = mutableNodes
                .Where(x => !string.IsNullOrWhiteSpace(x.Id))
                .Select(x => new DownstreamNodeProposalPayload
                {
                    Id = x.Id,
                    Label = x.Label,
                    AgentId = x.AgentId,
                    CapabilityId = x.CapabilityId,
                    InputSource = x.InputSource,
                    ExpectedOutput = x.ExpectedOutput,
                    Status = string.IsNullOrWhiteSpace(x.Status) ? "proposed" : x.Status
                })
                .ToList();

            if (nodes.Count == 0)
                return null;

            var edges = new List<DownstreamNodeEdgeProposalPayload>();
            for (int i = 1; i < nodes.Count; i++)
            {
                edges.Add(new DownstreamNodeEdgeProposalPayload
                {
                    FromNodeId = nodes[i - 1].Id,
                    ToNodeId = nodes[i].Id
                });
            }

            return new DownstreamNodePlanPayload
            {
                Status = "proposal",
                PipelineId = pipelineId,
                TaskType = taskType,
                SourceNodeId = sourceNodeId ?? "",
                CreatesCanvasNodes = false,
                ProposedNodes = nodes,
                ProposedEdges = edges,
                Notes = new[]
                {
                    "Materialized from workspace downstream_node_plan card.",
                    "Created nodes do not auto-run by default."
                }
            };
        }

        private static string ExtractSlashField(string line, string key)
        {
            if (string.IsNullOrWhiteSpace(line) || string.IsNullOrWhiteSpace(key))
                return "";

            var match = Regex.Match(
                line,
                $@"(?:^|/)\s*{Regex.Escape(key)}\s*=\s*(?<value>[^/]+)",
                RegexOptions.IgnoreCase);

            return match.Success
                ? match.Groups["value"].Value.Trim()
                : "";
        }

        private sealed class MutableDownstreamNodeProposal
        {
            public string Id { get; set; } = "";
            public string Label { get; set; } = "";
            public string AgentId { get; set; } = "";
            public string CapabilityId { get; set; } = "";
            public string InputSource { get; set; } = "";
            public string ExpectedOutput { get; set; } = "";
            public string Status { get; set; } = "proposed";
        }

        private static NodeTaskMode ResolveTaskModeForDownstreamProposal(
            DownstreamNodeProposalPayload proposal,
            AgentDefinition agent)
        {
            string capability = proposal?.CapabilityId ?? "";
            string label = proposal?.Label ?? "";

            if (capability.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                label.Contains("research", StringComparison.OrdinalIgnoreCase))
            {
                return NodeTaskMode.Research;
            }

            if (capability.Contains("generation", StringComparison.OrdinalIgnoreCase) ||
                capability.Contains("planning", StringComparison.OrdinalIgnoreCase))
            {
                return NodeTaskMode.Summarize;
            }

            return NodeTaskModeHelper.Normalize(agent.DefaultTaskMode);
        }

        private static string BuildDownstreamNodePrompt(
            DownstreamNodeProposalPayload proposal)
        {
            string label = BuildDownstreamNodeShortLabel(proposal);
            string detailLabel = string.IsNullOrWhiteSpace(proposal?.Label)
                ? "Downstream step"
                : proposal.Label.Trim();

            return
                $"{label}｜{TranslateDownstreamStepLabel(detailLabel)}\n\n" +
                "{{input}}";
        }

        private static string TranslateDownstreamStepLabel(string label)
        {
            string value = label?.Trim() ?? "";

            if (value.Contains("Research", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("source facts", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("supporting facts", StringComparison.OrdinalIgnoreCase))
                return "搜尋並驗證資料";

            if (value.Contains("outline", StringComparison.OrdinalIgnoreCase))
                return "建立大綱";

            // export 要先於 deck 判斷，否則 "Export deck" 會被翻成「生成簡報」。
            if (value.Contains("export", StringComparison.OrdinalIgnoreCase))
                return "匯出檔案";

            if (value.Contains("deck", StringComparison.OrdinalIgnoreCase) ||
                value.Contains("presentation", StringComparison.OrdinalIgnoreCase))
                return "生成簡報";

            if (value.Contains("draft", StringComparison.OrdinalIgnoreCase))
                return "撰寫草稿";

            if (value.Contains("decompose", StringComparison.OrdinalIgnoreCase))
                return "拆解任務";

            if (value.Contains("execute", StringComparison.OrdinalIgnoreCase))
                return "執行步驟";

            if (value.Contains("synthesize", StringComparison.OrdinalIgnoreCase))
                return "整合結果";

            // prompt 要先於 image 判斷（"Refine image prompt" 兩者都含）；
            // brief 要先於 video 判斷（"Create video brief" 兩者都含）。
            if (value.Contains("prompt", StringComparison.OrdinalIgnoreCase))
                return "優化圖片提示";

            if (value.Contains("image", StringComparison.OrdinalIgnoreCase))
                return "生成圖片";

            if (value.Contains("brief", StringComparison.OrdinalIgnoreCase))
                return "建立影片企劃";

            if (value.Contains("video", StringComparison.OrdinalIgnoreCase))
                return "生成影片";

            return string.IsNullOrWhiteSpace(value) ? "下游步驟" : value;
        }

        private static string BuildDownstreamNodeShortLabel(DownstreamNodeProposalPayload proposal)
        {
            string id = proposal?.Id?.Trim() ?? "";

            if (id.Equals("research", StringComparison.OrdinalIgnoreCase))
                return "Research";

            if (id.Equals("outline", StringComparison.OrdinalIgnoreCase))
                return "Outline";

            if (id.Equals("deck", StringComparison.OrdinalIgnoreCase))
                return "Deck";

            if (id.Equals("export", StringComparison.OrdinalIgnoreCase))
                return "Export";

            if (id.Equals("draft", StringComparison.OrdinalIgnoreCase))
                return "Draft";

            if (id.Equals("synthesize", StringComparison.OrdinalIgnoreCase))
                return "Synthesize";

            if (id.Equals("decompose", StringComparison.OrdinalIgnoreCase))
                return "Decompose";

            if (id.Equals("execute", StringComparison.OrdinalIgnoreCase))
                return "Execute";

            if (id.Equals("prompt", StringComparison.OrdinalIgnoreCase))
                return "Prompt";

            if (id.Equals("image", StringComparison.OrdinalIgnoreCase))
                return "Image";

            if (id.Equals("brief", StringComparison.OrdinalIgnoreCase))
                return "Brief";

            if (id.Equals("video", StringComparison.OrdinalIgnoreCase))
                return "Video";

            if (!string.IsNullOrWhiteSpace(proposal?.Label))
                return proposal.Label.Trim();

            return string.IsNullOrWhiteSpace(id) ? "Step" : id;
        }

        private void UpdateConnectionsFor(NodeControl node)
        {
            foreach (var c in _connections)
                if (ReferenceEquals(c.StartNode, node) || ReferenceEquals(c.EndNode, node))
                    UpdateConnectionGeometry(c);
        }

        private void UpdateConnectionGeometry(Connection c)
        {
            var start = GetThumbCenterOnCanvas(c.StartNode, c.StartThumb);
            var end = GetThumbCenterOnCanvas(c.EndNode, c.EndThumb);

            var geometry = new System.Windows.Media.PathGeometry();
            var figure = new System.Windows.Media.PathFigure { StartPoint = start };
            var segment = new System.Windows.Media.BezierSegment
            {
                Point1 = new Point((start.X + end.X) / 2, start.Y),
                Point2 = new Point((start.X + end.X) / 2, end.Y),
                Point3 = end
            };
            figure.Segments.Add(segment);
            geometry.Figures.Add(figure);
            c.Path.Data = geometry;
        }

        public bool HasOutgoingConnections(NodeControl node)
        {
            foreach (var c in _connections)
                if (ReferenceEquals(c.StartNode, node))
                    return true;
            return false;
        }

        public bool IsInitialNode(NodeControl node)
            => ReferenceEquals(_initialNode, node);

        private string GetAttachmentFolderForFile(string filePath)
        {
            var baseName = DisplayNameFromPath(filePath);
            return System.IO.Path.Combine(AttachmentsRootDir, baseName);
        }

        private string? GetCurrentAttachmentFolder()
        {
            if (string.IsNullOrWhiteSpace(_currentFilePath)) return null;
            var folder = GetAttachmentFolderForFile(_currentFilePath);
            Directory.CreateDirectory(folder);
            return folder;
        }

        private static bool IsImageExt(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext is ".png" or ".jpg" or ".jpeg" or ".webp" or ".bmp" or ".gif";
        }

        private static string MimeFromExt(string ext)
        {
            ext = (ext ?? "").ToLowerInvariant();
            return ext switch
            {
                ".png" => "image/png",
                ".jpg" => "image/jpeg",
                ".jpeg" => "image/jpeg",
                ".webp" => "image/webp",
                ".bmp" => "image/bmp",
                ".gif" => "image/gif",
                ".pdf" => "application/pdf",
                ".txt" => "text/plain",
                ".md" => "text/markdown",
                ".csv" => "text/csv",
                ".json" => "application/json",
                _ => "application/octet-stream"
            };
        }

        public void AddAttachmentsForNode(NodeControl node, IEnumerable<string> filePaths)
        {
            var folder = GetCurrentAttachmentFolder();
            if (folder == null)
            {
                MessageBox.Show("目前尚未建立檔案，無法上傳附件。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            if (!_attachmentsByNode.TryGetValue(node.Id, out var list))
            {
                list = new List<AttachmentInfo>();
                _attachmentsByNode[node.Id] = list;
            }

            foreach (var src in filePaths ?? Array.Empty<string>())
            {
                try
                {
                    if (!File.Exists(src)) continue;

                    var ext = System.IO.Path.GetExtension(src);
                    var mime = MimeFromExt(ext);
                    var kind = IsImageExt(ext) ? "image" : "file";

                    var safeName = System.IO.Path.GetFileName(src);
                    var uniqueName = $"{Guid.NewGuid():N}_{safeName}";
                    var dest = System.IO.Path.Combine(folder, uniqueName);

                    File.Copy(src, dest, overwrite: false);

                    var rel = System.IO.Path.Combine(DisplayNameFromPath(_currentFilePath!), uniqueName);

                    list.Add(new AttachmentInfo
                    {
                        FileName = safeName,
                        RelativePath = rel,
                        MimeType = mime,
                        Kind = kind
                    });
                }
                catch { }
            }

            SaveState();
        }

        public IReadOnlyList<AttachmentInfo> GetAttachmentsForNode(NodeControl node)
        {
            if (node == null)
                return Array.Empty<AttachmentInfo>();

            if (_attachmentsByNode.TryGetValue(node.Id, out var list))
                return list.ToList();

            return Array.Empty<AttachmentInfo>();
        }

        public void OpenAttachment(string relativePath)
        {
            try
            {
                var abs = System.IO.Path.Combine(AttachmentsRootDir, relativePath);
                if (!File.Exists(abs))
                {
                    MessageBox.Show("找不到附件檔案（可能已被移動或刪除）。", "開啟失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = abs,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"開啟附件失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>用系統預設程式開啟生成的檔案（報告 / 簡報 deck）。限制只能開啟 _generated 資料夾內的檔案。</summary>
        public void OpenGeneratedFile(string fullPath)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(fullPath) || !File.Exists(fullPath))
                {
                    MessageBox.Show("找不到生成的檔案（可能已被移動或刪除）。", "開啟失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                // 安全檢查：只允許開啟 _generated 資料夾內的檔案。
                string rootFull = System.IO.Path.GetFullPath(GeneratedFilesDir);
                string targetFull = System.IO.Path.GetFullPath(fullPath);
                if (!targetFull.StartsWith(rootFull, StringComparison.OrdinalIgnoreCase))
                {
                    MessageBox.Show("檔案不在允許開啟的範圍內。", "開啟失敗", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                Process.Start(new ProcessStartInfo
                {
                    FileName = targetFull,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"開啟檔案失敗：{ex.Message}", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        public void RemoveAttachment(NodeControl node, string relativePath)
        {
            if (!_attachmentsByNode.TryGetValue(node.Id, out var list))
                return;

            var hit = list.FirstOrDefault(a => string.Equals(a.RelativePath, relativePath, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return;

            try
            {
                var abs = System.IO.Path.Combine(AttachmentsRootDir, hit.RelativePath);
                if (File.Exists(abs))
                {
                    File.Delete(abs);
                }
            }
            catch { }

            list.Remove(hit);
            if (list.Count == 0)
                _attachmentsByNode.Remove(node.Id);

            SaveState();
        }

        private static string ReplaceAttachmentRelativeBase(string relativePath, string oldBaseName, string newBaseName)
        {
            if (string.IsNullOrWhiteSpace(relativePath))
                return relativePath ?? "";

            var normalized = relativePath.Replace('/', '\\');
            var oldPrefix = oldBaseName + "\\";

            if (normalized.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return newBaseName + normalized.Substring(oldBaseName.Length);
            }

            var fileName = System.IO.Path.GetFileName(normalized);
            if (string.IsNullOrWhiteSpace(fileName))
                return System.IO.Path.Combine(newBaseName, normalized);

            return System.IO.Path.Combine(newBaseName, fileName);
        }

        private void UpdateAttachmentRelativePathsInMemory(string oldBaseName, string newBaseName)
        {
            foreach (var kv in _attachmentsByNode)
            {
                foreach (var a in kv.Value)
                {
                    a.RelativePath = ReplaceAttachmentRelativeBase(a.RelativePath, oldBaseName, newBaseName);
                }
            }
        }

        private void RewriteAttachmentRelativePathsOnDisk(string filePath, string oldBaseName, string newBaseName)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                var json = File.ReadAllText(filePath);
                var state = JsonSerializer.Deserialize<AppState>(json);
                if (state == null) return;

                var newAttachments = (state.Attachments ?? new List<AttachmentState>())
                    .Select(a => new AttachmentState(
                        a.NodeId,
                        a.FileName,
                        ReplaceAttachmentRelativeBase(a.RelativePath, oldBaseName, newBaseName),
                        a.MimeType,
                        a.Kind))
                    .ToList();

                var rewritten = state with { Attachments = newAttachments };
                var newJson = JsonSerializer.Serialize(rewritten, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, newJson);
            }
            catch { }
        }

        private void SaveState()
        {
            if (!_hasStarted) return;
            if (_suppressSave) return;

            var nodes = new List<NodeState>();
            foreach (var child in MainCanvas.Children.OfType<NodeControl>())
            {
                double x = SafeFinite(Canvas.GetLeft(child), 0);
                double y = SafeFinite(Canvas.GetTop(child), 0);
                double width = SafePositiveFinite(child.Width, 300);
                double height = SafePositiveFinite(child.Height, 400);
                double fontSize = SafePositiveFinite(child.GetFontSize(), 20);

                nodes.Add(new NodeState(
    child.Id.ToString(),
    x,
    y,
    width,
    height,
    child.GetTopText(),
    child.GetBottomText(),
    child.GetTopLocked(),
    fontSize,
    GetNodeSelectedAgent(child),
    GetNodeSelectedModel(child),
    GetNodeTaskModeStorageValue(child),
    _unsupportedDownstreamNodeIds.Contains(child.Id)
));
            }

            var conns = new List<ConnState>();
            foreach (var c in _connections)
            {
                conns.Add(new ConnState(
                    c.StartNode.Id.ToString(),
                    c.EndNode.Id.ToString(),
                    c.StartThumb,
                    c.EndThumb
                ));
            }

            var atts = new List<AttachmentState>();
            foreach (var kv in _attachmentsByNode)
            {
                var nodeId = kv.Key.ToString();
                foreach (var a in kv.Value)
                {
                    atts.Add(new AttachmentState(
                        nodeId,
                        a.FileName,
                        a.RelativePath,
                        a.MimeType,
                        a.Kind
                    ));
                }
            }

            var logs = new List<ExecutionLogState>();

            foreach (var child in MainCanvas.Children.OfType<NodeControl>())
            {
                var nodeLogs = GetExecutionLogs(child);
                foreach (var log in nodeLogs)
                {
                    logs.Add(ToExecutionLogState(log));
                }
            }

            var state = new AppState(
    DateTime.Now,
    _initialNode?.Id.ToString(),
    nodes,
    conns,
    atts,
    logs,
    FileNameLocked: _fileNameLockedByUser,
    AutoModelSelectionEnabled: _isAutoModelSelectionEnabled,
    AdvancedAutoResolverEnabled: _isAdvancedAutoResolverEnabled
);

            if (string.IsNullOrEmpty(_currentFilePath))
                _currentFilePath = System.IO.Path.Combine(SavesDir, DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");

            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_currentFilePath!, json);

            CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";
        }

        private void LoadState(string path)
        {
            if (!File.Exists(path)) return;

            string json = File.ReadAllText(path);
            var state = JsonSerializer.Deserialize<AppState>(json);
            if (state == null) return;

            StartUI.Visibility = Visibility.Collapsed;
            MainUI.Visibility = Visibility.Visible;
            _hasStarted = true;
            _currentFilePath = path;
            CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";

            _fileNameLockedByUser = state.FileNameLocked;
            _isAutoModelSelectionEnabled = state.AutoModelSelectionEnabled;
            _isAdvancedAutoResolverEnabled = _isAutoModelSelectionEnabled && state.AdvancedAutoResolverEnabled;
            _lastAppliedAutoKeyword = "";
            _lastInitialTopSnapshot = "";

            if (AutoModelSwitch != null)
                AutoModelSwitch.IsChecked = _isAutoModelSelectionEnabled;

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();

            if (AdvancedAutoResolverSwitch != null)
                AdvancedAutoResolverSwitch.IsChecked = _isAdvancedAutoResolverEnabled;

            UpdateAdvancedAutoResolverVisibility();
            UpdateDecisionPanelForCurrentMode();

            _attachmentsByNode.Clear();
            _nodeModelsById.Clear();
            _nodeTaskModesById.Clear();
            _executionLogService.ClearAll();
            _nodeAgentsById.Clear();
            _unsupportedDownstreamNodeIds.Clear();

            _hoveredDecisionNode = null;
            _lastDecisionNode = null;

            _editingNode = null;
            _editingReason = EditReason.None;

            _suppressSave = true;
            try
            {
                ClearAll();

                foreach (var a in state.Attachments ?? new List<AttachmentState>())
                {
                    if (!Guid.TryParse(a.NodeId, out var gid)) continue;

                    if (!_attachmentsByNode.TryGetValue(gid, out var list))
                    {
                        list = new List<AttachmentInfo>();
                        _attachmentsByNode[gid] = list;
                    }

                    list.Add(new AttachmentInfo
                    {
                        FileName = a.FileName ?? "",
                        RelativePath = a.RelativePath ?? "",
                        MimeType = string.IsNullOrWhiteSpace(a.MimeType) ? "application/octet-stream" : a.MimeType,
                        Kind = string.IsNullOrWhiteSpace(a.Kind) ? "file" : a.Kind
                    });
                }

                foreach (var logState in state.ExecutionLogs ?? new List<ExecutionLogState>())
                {
                    if (string.IsNullOrWhiteSpace(logState.NodeId))
                        continue;

                    AddExecutionLog(ToExecutionLogEntry(logState));
                }

                var idMap = new Dictionary<string, NodeControl>();

                foreach (var n in state.Nodes)
                {
                    var node = new NodeControl(n.Id);
                    node.Width = SafePositiveFinite(n.Width, 300);
                    node.Height = SafePositiveFinite(n.Height, 400);
                    Canvas.SetLeft(node, SafeFinite(n.X, 0));
                    Canvas.SetTop(node, SafeFinite(n.Y, 0));
                    Canvas.SetZIndex(node, GetNextZIndex());
                    MainCanvas.Children.Add(node);
                    HookNode(node);

                    string loadedAgentId = NormalizeOrDefaultAgentId(n.AgentId);
                    _nodeAgentsById[node.Id] = loadedAgentId;

                    var loadedAgent = AgentRegistry.Get(loadedAgentId);

                    string loadedModel = string.IsNullOrWhiteSpace(n.NodeModel)
                        ? AiModelHelper.NormalizeNodeModel(loadedAgent.DefaultModelId)
                        : _aiRouter.NormalizeNodeModel(n.NodeModel);

                    _nodeModelsById[node.Id] = loadedModel;

                    _nodeTaskModesById[node.Id] = string.IsNullOrWhiteSpace(n.TaskMode)
                        ? NodeTaskModeHelper.Normalize(loadedAgent.DefaultTaskMode)
                        : ParseNodeTaskMode(n.TaskMode);

                    if (n.UnsupportedDownstreamNode)
                        _unsupportedDownstreamNodeIds.Add(node.Id);

                    node.SetCommittedModelId(loadedModel, syncEditingModel: true);

                    node.SetTopText(n.TopText ?? "");
                    node.SetBottomText(n.BottomText ?? "");
                    SyncAutoFlowTemplate(node, n.TopText ?? "");
                    node.SetTopLocked(n.TopLocked);
                    node.SetFontSize(SafePositiveFinite(n.FontSize, 20));

                    idMap[n.Id] = node;
                }

                _initialNode = null;
                if (!string.IsNullOrWhiteSpace(state.InitialNodeId) && idMap.TryGetValue(state.InitialNodeId!, out var bySaved))
                {
                    _initialNode = bySaved;
                }
                else
                {
                    var incoming = new HashSet<string>(state.Connections.Select(c => c.EndId));
                    var rootId = state.Nodes.Select(n => n.Id).FirstOrDefault(id => !incoming.Contains(id));
                    if (rootId != null && idMap.TryGetValue(rootId, out var byInference))
                        _initialNode = byInference;
                    else if (state.Nodes.Count > 0 && idMap.TryGetValue(state.Nodes[0].Id, out var byFirst))
                        _initialNode = byFirst;
                }

                Dispatcher.InvokeAsync(() =>
                {
                    foreach (var c in state.Connections)
                    {
                        if (!idMap.TryGetValue(c.StartId, out var sn)) continue;
                        if (!idMap.TryGetValue(c.EndId, out var en)) continue;
                        CreateCurve(sn, c.StartThumb, en, c.EndThumb);
                    }

                    RefreshAllNodeAttachmentUIs();
                }, DispatcherPriority.Loaded);
            }
            finally
            {
                _suppressSave = false;
                SaveState();
                RefreshFileList();
                SelectFileInList(path);
                RefreshAllNodeModelSelectionUIs();

                Dispatcher.BeginInvoke(new Action(() =>
                {
                    RestoreDecisionPanelAfterLoad();
                }), DispatcherPriority.Loaded);
            }

        }

        private void ClearAll()
        {
            MainCanvas.Children.Clear();
            _connections.Clear();
            _zIndexCounter = 0;
            _initialNode = null;
            _nodeModelsById.Clear();
            _nodeTaskModesById.Clear();
            _executionLogService.ClearAll();
            _autoFlowTemplatesByNode.Clear();
            _autoFlowPoliciesByNode.Clear();
            _unsupportedDownstreamNodeIds.Clear();
            _nodeAgentsById.Clear();
            _editingNode = null;
            _editingReason = EditReason.None;
        }

        private void ScheduleAutoRenameFromInitialNode(NodeControl initialNode)
        {
            if (!_hasStarted) return;
            if (_suppressSave) return;
            if (_fileNameLockedByUser) return;
            if (string.IsNullOrWhiteSpace(_currentFilePath)) return;

            var top = (initialNode.GetTopText() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(top)) return;

            if (string.Equals(_lastInitialTopSnapshot, top, StringComparison.Ordinal))
                return;

            _lastInitialTopSnapshot = top;

            _autoRenameTimer ??= new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(800)
            };

            _autoRenameTimer.Stop();
            _autoRenameTimer.Tick -= AutoRenameTimer_Tick;
            _autoRenameTimer.Tick += AutoRenameTimer_Tick;
            _autoRenameTimer.Start();

            void AutoRenameTimer_Tick(object? s, EventArgs e)
            {
                _autoRenameTimer?.Stop();
                _ = TryAutoRenameWithChatGPTStyleAsync(initialNode, top);
            }
        }

        private async Task TryAutoRenameWithChatGPTStyleAsync(NodeControl initialNode, string topSnapshot)
        {
            if (!_hasStarted) return;
            if (_suppressSave) return;
            if (_fileNameLockedByUser) return;
            if (string.IsNullOrWhiteSpace(_currentFilePath)) return;

            if (!File.Exists(_currentFilePath))
                return;

            string originalPath = _currentFilePath;

            try { _autoRenameCts?.Cancel(); } catch { }
            _autoRenameCts?.Dispose();
            _autoRenameCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ct = _autoRenameCts.Token;

            string keyword;

            try
            {
                keyword = await GenerateFileKeywordByAIAsync(initialNode, topSnapshot, ct).ConfigureAwait(true);
            }
            catch
            {
                keyword = ExtractFirstNonEmptyLine(topSnapshot);
            }

            keyword = NormalizeKeywordForFileName(keyword);
            if (string.IsNullOrWhiteSpace(keyword))
                return;

            if (string.Equals(_lastAppliedAutoKeyword, keyword, StringComparison.OrdinalIgnoreCase))
                return;

            var currentName = System.IO.Path.GetFileNameWithoutExtension(_currentFilePath);
            if (string.Equals(currentName, keyword, StringComparison.OrdinalIgnoreCase))
            {
                _lastAppliedAutoKeyword = keyword;
                return;
            }

            var desiredPath = System.IO.Path.Combine(SavesDir, keyword + ".json");
            var newPath = EnsureUniquePath(desiredPath);

            try
            {
                var oldBaseName = DisplayNameFromPath(_currentFilePath);
                var newBaseName = DisplayNameFromPath(newPath);

                var oldFolder = GetAttachmentFolderForFile(_currentFilePath);
                var newFolder = GetAttachmentFolderForFile(newPath);

                File.Move(_currentFilePath, newPath);

                bool attachmentFolderHandled = MoveAttachmentFolderSafely(oldFolder, newFolder, out var folderMoveError);
                if (!attachmentFolderHandled)
                {
                    try
                    {
                        if (File.Exists(newPath) && !File.Exists(originalPath))
                            File.Move(newPath, originalPath);
                    }
                    catch { }

                    Debug.WriteLine("Auto rename aborted because attachment folder move failed: " + folderMoveError);
                    return;
                }

                _currentFilePath = newPath;

                UpdateAttachmentRelativePathsInMemory(oldBaseName, newBaseName);
                RefreshAllNodeAttachmentUIs();
                SaveState();

                _lastAppliedAutoKeyword = keyword;

                CurrentFileLabel.Text = $"目前檔案：{DisplayNameFromPath(_currentFilePath)}";
                RefreshFileList();
                SelectFileInList(newPath);
            }
            catch { }
        }

        private async Task<string> GenerateFileKeywordByAIAsync(NodeControl node, string topText, CancellationToken ct)
        {
            if (string.IsNullOrWhiteSpace(topText))
                return "";

            string model = GetEffectiveNodeModel(node, topText);
            var route = _aiRouter.GetRouteInfo(model);

            string instructions = "你是一個善於替筆記自動命名的助手。";

            string user =
$@"請將下面內容，取一個像 ChatGPT 自動命名筆記那樣的「短標題/關鍵字」：
- 使用繁體中文
- 盡量 6~16 字
- 只輸出標題本身，不要加引號、不要加編號、不要加任何解釋
- 不要包含檔案副檔名

內容：
{Truncate(topText.Trim(), 800)}";

            switch (route.Provider)
            {
                case AiProviderKind.PerplexitySonar:
                    {
                        var svc = _aiRouter.GetPerplexitySonarService(route.ServiceModel);
                        var text = await svc.GenerateAsync(
                            instructions,
                            user,
                            maxOutputTokens: 200,
                            ct: ct);

                        return (text ?? "").Trim();
                    }

                case AiProviderKind.Claude:
                    {
                        var text = await _aiRouter.GetClaudeService(route.NodeModel).GenerateAsync(
                            instructions,
                            user,
                            maxOutputTokens: 200,
                            ct: ct);

                        return (text ?? "").Trim();
                    }

                case AiProviderKind.OpenAI:
                default:
                    {
                        var text = await _aiRouter.GetOpenAiService(route.NodeModel).GenerateAsync(
                            instructions,
                            user,
                            maxOutputTokens: 200,
                            ct: ct);

                        return (text ?? "").Trim();
                    }
            }
        }

        private static string ExtractFirstNonEmptyLine(string text)
        {
            foreach (var line in (text ?? "").Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                var t = line.Trim();
                if (!string.IsNullOrWhiteSpace(t))
                    return t;
            }
            return "";
        }

        private static string NormalizeKeywordForFileName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            var s = raw.Trim();

            s = s.Trim('\"', '“', '”', '\'', '『', '』', '「', '」');
            s = Regex.Replace(s, @"\s+", " ");

            foreach (var ch in System.IO.Path.GetInvalidFileNameChars())
                s = s.Replace(ch, '_');

            s = s.Trim().TrimEnd('.');
            s = s.Trim('_');

            const int maxLen = 28;
            if (s.Length > maxLen)
                s = s.Substring(0, maxLen).Trim();

            return s;
        }

        private string EnsureUniquePath(string desiredFullPath)
        {
            if (!File.Exists(desiredFullPath))
                return desiredFullPath;

            var dir = System.IO.Path.GetDirectoryName(desiredFullPath) ?? SavesDir;
            var baseName = System.IO.Path.GetFileNameWithoutExtension(desiredFullPath);
            var ext = System.IO.Path.GetExtension(desiredFullPath);

            for (int i = 2; i < 9999; i++)
            {
                var candidate = System.IO.Path.Combine(dir, $"{baseName}_{i}{ext}");
                if (!File.Exists(candidate))
                    return candidate;
            }

            return desiredFullPath;
        }

        private void MarkFileNameLockedOnDisk(string filePath)
        {
            try
            {
                if (!File.Exists(filePath)) return;

                var json = File.ReadAllText(filePath);
                var state = JsonSerializer.Deserialize<AppState>(json);
                if (state == null) return;

                if (state.FileNameLocked) return;

                var locked = state with { FileNameLocked = true };
                var newJson = JsonSerializer.Serialize(locked, new JsonSerializerOptions { WriteIndented = true });
                File.WriteAllText(filePath, newJson);
            }
            catch { }
        }

        private void CenterOnInitialButton_Click(object sender, RoutedEventArgs e)
        {
            if (_initialNode == null) return;

            double nodeX = Canvas.GetLeft(_initialNode) + _initialNode.Width / 2;
            double nodeY = Canvas.GetTop(_initialNode) + _initialNode.Height / 2;

            double viewportCenterX = Viewport.ActualWidth / 2;
            double viewportCenterY = Viewport.ActualHeight / 2;

            double targetX = viewportCenterX - nodeX * 1.0;
            double targetY = viewportCenterY - nodeY * 1.0;

            var animScale = new DoubleAnimation
            {
                To = 1.0,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            var animX = new DoubleAnimation
            {
                To = targetX,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };
            var animY = new DoubleAnimation
            {
                To = targetY,
                Duration = TimeSpan.FromMilliseconds(400),
                EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut }
            };

            animY.Completed += (s, _) =>
            {
                _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                _translateTransform.BeginAnimation(TranslateTransform.XProperty, null);
                _translateTransform.BeginAnimation(TranslateTransform.YProperty, null);

                _scaleTransform.ScaleX = 1.0;
                _scaleTransform.ScaleY = 1.0;
                _translateTransform.X = targetX;
                _translateTransform.Y = targetY;

                _scale = 1.0;
            };

            _scaleTransform.BeginAnimation(ScaleTransform.ScaleXProperty, animScale);
            _scaleTransform.BeginAnimation(ScaleTransform.ScaleYProperty, animScale);
            _translateTransform.BeginAnimation(TranslateTransform.XProperty, animX);
            _translateTransform.BeginAnimation(TranslateTransform.YProperty, animY);
        }

        private void CollapseSidebar(object sender, RoutedEventArgs e)
        {
            var animSidebar = new DoubleAnimation { From = Sidebar.ActualWidth, To = 0, Duration = TimeSpan.FromMilliseconds(300) };
            var animViewport = new DoubleAnimation { From = 140, To = 0, Duration = TimeSpan.FromMilliseconds(300) };

            animSidebar.Completed += (s, _) =>
            {
                Sidebar.Visibility = Visibility.Collapsed;
                HotZoneContainer.Visibility = Visibility.Visible;
                _isSidebarCollapsed = true;
            };

            Sidebar.BeginAnimation(WidthProperty, animSidebar);
            ViewportTranslate.BeginAnimation(TranslateTransform.XProperty, animViewport);
        }

        private void ExpandSidebar(object sender, RoutedEventArgs e)
        {
            Sidebar.Visibility = Visibility.Visible;
            var animSidebar = new DoubleAnimation { From = 0, To = 280, Duration = TimeSpan.FromMilliseconds(300) };
            var animViewport = new DoubleAnimation { From = 0, To = 140, Duration = TimeSpan.FromMilliseconds(300) };

            animSidebar.Completed += (s, _) =>
            {
                HotZoneContainer.Visibility = Visibility.Collapsed;
                _isSidebarCollapsed = false;
            };

            Sidebar.BeginAnimation(WidthProperty, animSidebar);
            ViewportTranslate.BeginAnimation(TranslateTransform.XProperty, animViewport);
        }

        private void Viewport_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle)
            {
                _isPanning = true;
                _lastMousePos = e.GetPosition(this);
                Viewport.CaptureMouse();
            }
            else if (!_isSidebarCollapsed)
            {
                CollapseSidebar(sender, e);
            }
        }

        private void Viewport_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton == MouseButton.Middle && _isPanning)
            {
                _isPanning = false;
                Viewport.ReleaseMouseCapture();
            }
        }

        private void Viewport_MouseMove(object sender, MouseEventArgs e)
        {
            if (_isPanning)
            {
                var pos = e.GetPosition(this);
                var delta = pos - _lastMousePos;
                _translateTransform.X += delta.X;
                _translateTransform.Y += delta.Y;
                _lastMousePos = pos;
            }
        }

        private static T? FindAncestor<T>(DependencyObject? current) where T : DependencyObject
        {
            while (current != null)
            {
                if (current is T hit)
                    return hit;

                current = VisualTreeHelper.GetParent(current);
            }

            return null;
        }

        private void Viewport_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            var original = e.OriginalSource as DependencyObject;

            if (FindAncestor<NodeControl>(original) != null)
                return;

            double zoom = e.Delta > 0 ? 1.1 : 1 / 1.1;
            double newScale = _scale * zoom;
            if (newScale < 0.1 || newScale > 6.0) return;

            var pos = e.GetPosition(MainCanvas);
            _translateTransform.X -= (pos.X * (newScale - _scale));
            _translateTransform.Y -= (pos.Y * (newScale - _scale));

            _scaleTransform.ScaleX = newScale;
            _scaleTransform.ScaleY = newScale;
            _scale = newScale;

            e.Handled = true;
        }

        private void HotZoneContainer_MouseEnter(object sender, MouseEventArgs e) { }

        private void AutoModelSwitch_Checked(object sender, RoutedEventArgs e)
        {
            SetAutoModelSelectionEnabled(true);
        }

        private void AutoModelSwitch_Unchecked(object sender, RoutedEventArgs e)
        {
            SetAutoModelSelectionEnabled(false);
        }

        private void AdvancedAutoResolverSwitch_Checked(object sender, RoutedEventArgs e)
        {
            SetAdvancedAutoResolverEnabled(true);
        }

        private void AdvancedAutoResolverSwitch_Unchecked(object sender, RoutedEventArgs e)
        {
            SetAdvancedAutoResolverEnabled(false);
        }

        public void DeleteNodeAndDescendants(NodeControl root)
        {
            var nodesToDelete = new HashSet<NodeControl>();
            var connsToDelete = new HashSet<Connection>();

            var stack = new Stack<NodeControl>();
            stack.Push(root);

            while (stack.Count > 0)
            {
                var n = stack.Pop();
                if (!nodesToDelete.Add(n)) continue;

                foreach (var c in _connections)
                {
                    if (ReferenceEquals(c.StartNode, n))
                    {
                        connsToDelete.Add(c);
                        stack.Push(c.EndNode);
                    }
                }

                foreach (var c in _connections)
                {
                    if (ReferenceEquals(c.EndNode, n))
                    {
                        connsToDelete.Add(c);
                    }
                }
            }

            foreach (var c in connsToDelete)
            {
                if (c.Path != null)
                    MainCanvas.Children.Remove(c.Path);
            }
            _connections.RemoveAll(c => connsToDelete.Contains(c));

            foreach (var n in nodesToDelete)
            {
                n.ClearOutputFiles();
                ClearEditingIfDeleted(n);
                MainCanvas.Children.Remove(n);
                _attachmentsByNode.Remove(n.Id);
                _nodeModelsById.Remove(n.Id);
                _nodeTaskModesById.Remove(n.Id);
                _autoFlowTemplatesByNode.Remove(n.Id);
                _autoFlowPoliciesByNode.Remove(n.Id);
                _unsupportedDownstreamNodeIds.Remove(n.Id);
                ClearExecutionLogs(n);
            }

            if (_initialNode != null && nodesToDelete.Contains(_initialNode))
                _initialNode = null;

            SaveState();
        }

        internal Canvas GetMainCanvasRef() => MainCanvas;

        internal string GetAttachmentsRootDir() => AttachmentsRootDir;

        internal IEnumerable<NodeControl> GetAllNodesInCanvas()
            => MainCanvas.Children.OfType<NodeControl>();

        internal IEnumerable<(NodeControl StartNode, string StartThumb, NodeControl EndNode, string EndThumb)> GetAllConnections()
        {
            foreach (var c in _connections)
                yield return (c.StartNode, c.StartThumb, c.EndNode, c.EndThumb);
        }

        public void SetLiveDecisionResolving(NodeControl node, NodeExecutionDecision decision)
        {
            if (node == null || decision == null)
                return;

            string requestedLabel = GetDecisionModelLabel(
                string.IsNullOrWhiteSpace(decision.RequestedModelId) ? decision.ModelId : decision.RequestedModelId);

            string plannedLabel = GetDecisionModelLabel(decision.ModelId);

            string modelText = string.Equals(requestedLabel, plannedLabel, StringComparison.OrdinalIgnoreCase)
                ? plannedLabel
                : $"{plannedLabel} ← {requestedLabel}";

            var steps = new List<NodeDecisionStepViewData>
    {
        new NodeDecisionStepViewData
        {
            Title = "Task Mode",
            Detail = $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / confidence {decision.Confidence:0.00}",
            State = NodeDecisionStepState.Info,
            Highlight = true,
            DetailLines = BuildTaskModeLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Model Selection",
            Detail = modelText,
            State = NodeDecisionStepState.Info,
            Highlight = true,
            DetailLines = BuildModelLines(decision, actualModelId: null)
        },
         new NodeDecisionStepViewData
{
    Title = "Resolver",
    Detail = string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel,
    State = NodeDecisionStepState.Info,
    Highlight = true,
    IsActive = true,
    DetailLines = BuildResolverLines(decision, extra: "正在建立執行決策")
},
        new NodeDecisionStepViewData
{
    Title = "Capability Guard",
    Detail = decision.CapabilityAdjusted ? "已調整" : "檢查中",
    State = decision.CapabilityAdjusted ? NodeDecisionStepState.Warning : NodeDecisionStepState.Info,
    IsActive = !decision.CapabilityAdjusted,
    DetailLines = BuildCapabilityLines(decision, forcePendingText: !decision.CapabilityAdjusted)
},
        new NodeDecisionStepViewData
{
    Title = "Delegation",
    Detail = "尚未觸發",
    State = NodeDecisionStepState.Info,
    DetailLines = new[]
    {
        "目前尚未進入 agent delegation"
    }
},
        new NodeDecisionStepViewData
        {
            Title = "Fallback",
            Detail = "尚未觸發",
            State = NodeDecisionStepState.Info,
            DetailLines = new[]
            {
                "目前尚未進入 runtime fallback"
            }
        },
       new NodeDecisionStepViewData
{
    Title = "Execution",
    Detail = "等待執行",
    State = NodeDecisionStepState.Info,
    DetailLines = new[]
    {
        "執行尚未開始"
    }
}
    };

            var view = BuildLiveDecisionViewData(
                decision,
                modelText,
                $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / {decision.Confidence:0.00}",
                extra: decision.CapabilityAdjusted
                    ? (string.IsNullOrWhiteSpace(decision.CapabilityReason) ? "-" : decision.CapabilityReason)
                    : "-",
                steps: steps);

            _liveDecisionViewsByNode[node.Id] = view;
            RefreshDecisionForNode(node);
        }

        public void SetLiveDecisionExecuting(
            NodeControl node,
            NodeExecutionDecision decision,
            string modelId,
            bool isFallbackAttempt,
            int attemptIndex,
            string reason)
        {
            if (node == null || decision == null)
                return;

            string requestedLabel = GetDecisionModelLabel(
                string.IsNullOrWhiteSpace(decision.RequestedModelId)
                    ? decision.ModelId
                    : decision.RequestedModelId);

            string plannedLabel = GetDecisionModelLabel(modelId);

            string modelText = string.Equals(requestedLabel, plannedLabel, StringComparison.OrdinalIgnoreCase)
                ? plannedLabel
                : $"{plannedLabel} ← {requestedLabel}";

            // ===== Capability =====
            var capabilityLines = new List<string>();
            string capabilityDetail;

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
            {
                capabilityDetail = AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace);
                capabilityLines.AddRange(AgentCapabilityTraceFormatter.BuildDetailLines(decision.CapabilityTrace));
            }
            else
            {
                capabilityDetail = decision.CapabilityAdjusted ? "已調整" : "OK";
                capabilityLines.AddRange(BuildCapabilityLines(decision, forcePendingText: false));
            }

            // ===== Delegation =====
            var delegationLines = new List<string>();
            string delegationDetail;

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
            {
                delegationDetail = AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace);
                delegationLines.AddRange(AgentDelegationTraceFormatter.BuildDetailLines(decision.DelegationTrace));
            }
            else
            {
                delegationDetail = "尚未觸發";
                delegationLines.Add("目前尚未進入 agent delegation");
            }

            // ===== Fallback =====
            var fallbackLines = new List<string>();

            if (isFallbackAttempt)
            {
                fallbackLines.Add($"目前為第 {attemptIndex} 次嘗試");
                fallbackLines.Add($"候選模型：{plannedLabel}");
                fallbackLines.Add($"原因：{(string.IsNullOrWhiteSpace(reason) ? "-" : reason)}");
            }
            else
            {
                fallbackLines.Add("目前使用 primary candidate 執行");
                fallbackLines.Add($"模型：{plannedLabel}");
            }

            if (decision.RuntimeFallbackAttempts != null && decision.RuntimeFallbackAttempts.Count > 0)
            {
                foreach (var attempt in decision.RuntimeFallbackAttempts)
                {
                    if (attempt == null)
                        continue;

                    string attemptModelLabel = GetDecisionModelLabel(attempt.ModelId);
                    string state = attempt.Success ? "Success" : "Failed";

                    fallbackLines.Add(
                        $"{attempt.AttemptIndex}. {attemptModelLabel} / {state} / {attempt.Reason} / {attempt.ErrorMessage}");
                }
            }

            // ===== Execution =====
            var executionLines = new List<string>
    {
        "Execution 狀態：執行中",
        $"Model：{plannedLabel}",
        $"Streaming：{decision.UseStreaming}"
    };

            var steps = new List<NodeDecisionStepViewData>
    {
        new NodeDecisionStepViewData
        {
            Title = "Task Mode",
            Detail = $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / confidence {decision.Confidence:0.00}",
            State = NodeDecisionStepState.Success,
            DetailLines = BuildTaskModeLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Model Selection",
            Detail = modelText,
            State = decision.CapabilityAdjusted || isFallbackAttempt
                ? NodeDecisionStepState.Warning
                : NodeDecisionStepState.Success,
            DetailLines = BuildModelLines(decision, actualModelId: modelId)
        },
        new NodeDecisionStepViewData
        {
            Title = "Resolver",
            Detail = string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel,
            State = NodeDecisionStepState.Success,
            DetailLines = BuildResolverLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Capability",
            Detail = capabilityDetail,
            State = decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0
                ? NodeDecisionStepState.Warning
                : (decision.CapabilityAdjusted ? NodeDecisionStepState.Warning : NodeDecisionStepState.Success),
            Highlight = decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0,
            DetailLines = capabilityLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Delegation",
            Detail = delegationDetail,
            State = decision.DelegationTrace != null && decision.DelegationTrace.Count > 0
                ? NodeDecisionStepState.Warning
                : NodeDecisionStepState.Info,
            Highlight = decision.DelegationTrace != null && decision.DelegationTrace.Count > 0,
            DetailLines = delegationLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Fallback",
            Detail = isFallbackAttempt ? $"已進入第 {attemptIndex} 次嘗試" : "尚未觸發",
            State = isFallbackAttempt ? NodeDecisionStepState.Warning : NodeDecisionStepState.Info,
            Highlight = isFallbackAttempt,
            IsActive = isFallbackAttempt,
            DetailLines = fallbackLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Execution",
            Detail = "執行中",
            State = NodeDecisionStepState.Info,
            Highlight = true,
            IsActive = true,
            DetailLines = executionLines
        }
    };

            var extraParts = new List<string>();

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
                extraParts.Add("capability: " + AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace));
            else if (decision.CapabilityAdjusted && !string.IsNullOrWhiteSpace(decision.CapabilityReason))
                extraParts.Add(decision.CapabilityReason);

            if (isFallbackAttempt)
                extraParts.Add($"fallback attempt {attemptIndex}");

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
                extraParts.Add("delegation: " + AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace));

            var view = BuildLiveDecisionViewData(
                decision,
                modelText,
                $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / 執行中",
                extra: extraParts.Count == 0 ? "-" : string.Join(" / ", extraParts),
                steps: steps);

            _liveDecisionViewsByNode[node.Id] = view;
            RefreshDecisionForNode(node);
        }
        public void SetLiveDecisionFailed(NodeControl node, NodeExecutionDecision decision, string errorMessage)
        {
            if (node == null || decision == null)
                return;

            string requestedLabel = GetDecisionModelLabel(
                string.IsNullOrWhiteSpace(decision.RequestedModelId)
                    ? decision.ModelId
                    : decision.RequestedModelId);

            string actualLabel = GetDecisionModelLabel(
                string.IsNullOrWhiteSpace(decision.ActualModelId)
                    ? decision.ModelId
                    : decision.ActualModelId);

            string modelText = string.Equals(requestedLabel, actualLabel, StringComparison.OrdinalIgnoreCase)
                ? actualLabel
                : $"{actualLabel} ← {requestedLabel}";

            // ===== Capability =====
            var capabilityLines = new List<string>();
            string capabilityDetail;

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
            {
                capabilityDetail = AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace);
                capabilityLines.AddRange(AgentCapabilityTraceFormatter.BuildDetailLines(decision.CapabilityTrace));
            }
            else
            {
                capabilityDetail = decision.CapabilityAdjusted ? "已調整" : "OK";
                capabilityLines.AddRange(BuildCapabilityLines(decision, forcePendingText: false));
            }

            // ===== Delegation =====
            var delegationLines = new List<string>();
            string delegationDetail;

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
            {
                delegationDetail = AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace);
                delegationLines.AddRange(AgentDelegationTraceFormatter.BuildDetailLines(decision.DelegationTrace));
            }
            else
            {
                delegationDetail = "尚未觸發";
                delegationLines.Add("目前尚未進入 agent delegation");
            }

            var steps = new List<NodeDecisionStepViewData>
    {
        new NodeDecisionStepViewData
        {
            Title = "Task Mode",
            Detail = $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / confidence {decision.Confidence:0.00}",
            State = NodeDecisionStepState.Success,
            DetailLines = BuildTaskModeLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Model Selection",
            Detail = modelText,
            State = decision.CapabilityAdjusted || decision.RuntimeFallbackUsed
                ? NodeDecisionStepState.Warning
                : NodeDecisionStepState.Success,
            DetailLines = BuildModelLines(decision, decision.ActualModelId)
        },
        new NodeDecisionStepViewData
        {
            Title = "Resolver",
            Detail = string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel,
            State = NodeDecisionStepState.Success,
            DetailLines = BuildResolverLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Capability",
            Detail = capabilityDetail,
            State = decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0
                ? NodeDecisionStepState.Warning
                : (decision.CapabilityAdjusted ? NodeDecisionStepState.Warning : NodeDecisionStepState.Success),
            Highlight = decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0,
            DetailLines = capabilityLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Delegation",
            Detail = delegationDetail,
            State = decision.DelegationTrace != null && decision.DelegationTrace.Count > 0
                ? NodeDecisionStepState.Warning
                : NodeDecisionStepState.Info,
            Highlight = decision.DelegationTrace != null && decision.DelegationTrace.Count > 0,
            DetailLines = delegationLines
        },
        new NodeDecisionStepViewData
        {
            Title = "Fallback",
            Detail = decision.RuntimeFallbackUsed ? "已觸發" : "無",
            State = decision.RuntimeFallbackUsed ? NodeDecisionStepState.Warning : NodeDecisionStepState.Success,
            DetailLines = BuildFallbackLines(decision)
        },
        new NodeDecisionStepViewData
        {
            Title = "Execution",
            Detail = "失敗",
            State = NodeDecisionStepState.Error,
            Highlight = true,
            DetailLines = new[]
            {
                $"Error: {errorMessage}"
            }
        }
    };

            var extraParts = new List<string>();

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
                extraParts.Add("capability: " + AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace));

            if (!string.IsNullOrWhiteSpace(errorMessage))
                extraParts.Add(errorMessage);

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
                extraParts.Add("delegation: " + AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace));

            var view = BuildLiveDecisionViewData(
                decision,
                modelText,
                $"{NodeTaskModeHelper.ToDisplayName(decision.TaskMode)} / 失敗",
                extra: extraParts.Count == 0 ? "-" : string.Join(" / ", extraParts),
                steps: steps);

            _liveDecisionViewsByNode[node.Id] = view;
            RefreshDecisionForNode(node);
        }
        public void ClearLiveDecisionState(NodeControl node)
        {
            if (node == null)
                return;

            if (_liveDecisionViewsByNode.Remove(node.Id))
                RefreshDecisionForNode(node);
        }

        private NodeDecisionViewData BuildLiveDecisionViewData(
    NodeExecutionDecision decision,
    string modelText,
    string taskSummary,
    string extra,
    IReadOnlyList<NodeDecisionStepViewData> steps)
        {
            string status = decision.StatusLabel;
            if (string.IsNullOrWhiteSpace(status))
                status = _isAutoModelSelectionEnabled ? "Auto" : "Manual";

            string mode = _isAutoModelSelectionEnabled ? "Auto" : "Manual";
            string resolver = string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel;

            string reason = string.IsNullOrWhiteSpace(decision.ResolverReason)
                ? "-"
                : decision.ResolverReason;

            string keywords = "-";
            if (decision.ResolverKeywords != null && decision.ResolverKeywords.Count > 0)
            {
                keywords = "keywords: " + string.Join(
                    ", ",
                    decision.ResolverKeywords
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase));
            }

            // ===== Capability =====
            string capabilitySummary = "-";
            var capabilityDetails = new List<string>();

            if (decision.CapabilityTrace != null && decision.CapabilityTrace.Count > 0)
            {
                capabilitySummary = AgentCapabilityTraceFormatter.BuildSummary(decision.CapabilityTrace);
                capabilityDetails = AgentCapabilityTraceFormatter.BuildDetailLines(decision.CapabilityTrace).ToList();
            }

            // ===== Delegation =====
            string delegationSummary = "-";
            var delegationDetails = new List<string>();

            if (decision.DelegationTrace != null && decision.DelegationTrace.Count > 0)
            {
                delegationSummary = AgentDelegationTraceFormatter.BuildSummary(decision.DelegationTrace);
                delegationDetails = AgentDelegationTraceFormatter.BuildDetailLines(decision.DelegationTrace).ToList();
            }

            string agent = string.IsNullOrWhiteSpace(decision.ActualAgentId)
                ? (string.IsNullOrWhiteSpace(decision.RequestedAgentId) ? "-" : decision.RequestedAgentId)
                : decision.ActualAgentId;

            return new NodeDecisionViewData
            {
                Status = status,
                Mode = mode,
                Resolver = resolver,
                Agent = agent,
                Model = modelText,
                TaskSummary = taskSummary,
                Reason = reason,
                Keywords = keywords,
                Extra = string.IsNullOrWhiteSpace(extra) ? "-" : extra,

                CapabilitySummary = capabilitySummary,
                CapabilityDetails = capabilityDetails,

                DelegationSummary = delegationSummary,
                DelegationDetails = delegationDetails,

                CapabilityAdjusted = decision.CapabilityAdjusted,
                RuntimeFallbackUsed = decision.RuntimeFallbackUsed,
                ApiFallbackUsed = decision.UsedFallbackToRules,
                Steps = AppendLiveWorkspaceStep(
                    steps,
                    decision.WorkspaceSummary ?? "",
                    decision.WorkspaceArtifactDetails ?? Array.Empty<string>())
            };
        }

        private static IReadOnlyList<NodeDecisionStepViewData> AppendLiveWorkspaceStep(
            IReadOnlyList<NodeDecisionStepViewData> steps,
            string workspaceSummary,
            IReadOnlyList<string> workspaceArtifactDetails)
        {
            var result = steps?.ToList() ?? new List<NodeDecisionStepViewData>();
            var detailLines = new List<string>();

            if (!string.IsNullOrWhiteSpace(workspaceSummary))
            {
                detailLines.AddRange(
                    workspaceSummary
                        .Replace("\r\n", "\n")
                        .Replace('\r', '\n')
                        .Split('\n')
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Select(x => x.Trim()));
            }

            if (workspaceArtifactDetails != null && workspaceArtifactDetails.Count > 0)
            {
                if (detailLines.Count > 0)
                    detailLines.Add("--- Artifacts ---");

                detailLines.AddRange(workspaceArtifactDetails.Where(x => !string.IsNullOrWhiteSpace(x)));
            }

            if (detailLines.Count == 0)
                return result;

            int artifactCount = workspaceArtifactDetails?
                .Count(x => x.StartsWith("Artifact:", StringComparison.OrdinalIgnoreCase)) ?? 0;

            int factCount = workspaceArtifactDetails?
                .Count(x => x.TrimStart().StartsWith("Fact:", StringComparison.OrdinalIgnoreCase)) ?? 0;

            result.Insert(Math.Max(0, result.Count - 2), new NodeDecisionStepViewData
            {
                Title = "Workspace",
                Detail = $"Artifacts: {artifactCount}, facts: {factCount}",
                State = factCount > 0 ? NodeDecisionStepState.Success : NodeDecisionStepState.Info,
                Highlight = true,
                DetailLines = detailLines
            });

            return result;
        }


        private static IReadOnlyList<string> BuildTaskModeLines(NodeExecutionDecision decision)
        {
            var lines = new List<string>
    {
        $"Task Mode: {NodeTaskModeHelper.ToDisplayName(decision.TaskMode)}",
        $"Confidence: {decision.Confidence:0.00}"
    };

            if (!string.IsNullOrWhiteSpace(decision.ResolverReason))
                lines.Add($"Reason: {decision.ResolverReason}");

            if (decision.ResolverKeywords != null && decision.ResolverKeywords.Count > 0)
            {
                lines.Add("Keywords: " + string.Join(
                    ", ",
                    decision.ResolverKeywords
                        .Where(x => !string.IsNullOrWhiteSpace(x))
                        .Distinct(StringComparer.OrdinalIgnoreCase)));
            }

            return lines;
        }

        private static IReadOnlyList<string> BuildModelLines(NodeExecutionDecision decision, string? actualModelId)
        {
            var lines = new List<string>
    {
        $"Requested Model: {GetDecisionModelLabel(string.IsNullOrWhiteSpace(decision.RequestedModelId) ? decision.ModelId : decision.RequestedModelId)}",
        $"Planned Model: {GetDecisionModelLabel(decision.ModelId)}"
    };

            if (!string.IsNullOrWhiteSpace(actualModelId))
                lines.Add($"Current/Actual Model: {GetDecisionModelLabel(actualModelId)}");

            return lines;
        }

        private static IReadOnlyList<string> BuildResolverLines(NodeExecutionDecision decision, string? extra = null)
        {
            var lines = new List<string>
    {
        $"Resolver: {(string.IsNullOrWhiteSpace(decision.ResolverLabel) ? "-" : decision.ResolverLabel)}",
        $"UsedApiResolver: {decision.UsedApiResolver}",
        $"UsedFallbackToRules: {decision.UsedFallbackToRules}"
    };

            if (!string.IsNullOrWhiteSpace(extra))
                lines.Add(extra);

            return lines;
        }

        private static IReadOnlyList<string> BuildCapabilityLines(NodeExecutionDecision decision, bool forcePendingText)
        {
            var lines = new List<string>();

            if (forcePendingText && !decision.CapabilityAdjusted)
            {
                lines.Add("Capability Guard: waiting / checking");
                return lines;
            }

            lines.Add($"Requested: {GetDecisionModelLabel(decision.CapabilityRequestedModelId)}");
            lines.Add($"Resolved: {GetDecisionModelLabel(decision.CapabilityResolvedModelId)}");
            lines.Add($"Required: {decision.CapabilityRequired}");
            lines.Add($"Missing: {decision.CapabilityMissing}");
            lines.Add($"StreamingAdjusted: {decision.CapabilityStreamingAdjusted}");

            if (!string.IsNullOrWhiteSpace(decision.CapabilityReason))
                lines.Add($"Reason: {decision.CapabilityReason}");

            return lines;
        }

        private static IReadOnlyList<string> BuildFallbackLines(NodeExecutionDecision decision)
        {
            var lines = new List<string>();

            if (decision.RuntimeFallbackUsed && !string.IsNullOrWhiteSpace(decision.RuntimeFallbackSummary))
                lines.Add($"Summary: {decision.RuntimeFallbackSummary}");

            if (decision.RuntimeFallbackAttempts != null && decision.RuntimeFallbackAttempts.Count > 0)
            {
                foreach (var attempt in decision.RuntimeFallbackAttempts)
                {
                    if (attempt == null)
                        continue;

                    string modelLabel = GetDecisionModelLabel(attempt.ModelId);
                    string state = attempt.Success ? "Success" : "Failed";

                    lines.Add($"{attempt.AttemptIndex}. {modelLabel} / {state} / {attempt.Reason} / {attempt.ErrorMessage}");
                }
            }

            if (lines.Count == 0)
                lines.Add("No fallback used.");

            return lines;
        }


        private static string GetDecisionModelLabel(string? modelId)
        {
            var def = AiModelHelper.GetDefinition(modelId);

            if (!string.IsNullOrWhiteSpace(def.DisplayName))
                return def.DisplayName;

            if (!string.IsNullOrWhiteSpace(def.Id))
                return def.Id;

            return AiModelRegistry.Default.DisplayName;
        }

        private void RestoreDecisionPanelAfterLoad()
        {
            NodeControl? target = null;

            if (_lastDecisionNode != null && MainCanvas.Children.Contains(_lastDecisionNode))
            {
                target = _lastDecisionNode;
            }
            else if (_hoveredDecisionNode != null && MainCanvas.Children.Contains(_hoveredDecisionNode))
            {
                target = _hoveredDecisionNode;
            }
            else if (_initialNode != null && MainCanvas.Children.Contains(_initialNode))
            {
                target = _initialNode;
            }
            else
            {
                target = MainCanvas.Children.OfType<NodeControl>().FirstOrDefault();
            }

            _hoveredDecisionNode = null;
            _lastDecisionNode = target;

            if (target != null)
                ShowDecisionForNode(target);
            else
                UpdateDecisionPanelForCurrentMode();
        }

        private class SimpleInputDialog : Window
        {
            private readonly TextBox _tb;
            private string _result = "";

            private SimpleInputDialog(string title, string prompt, string defaultValue)
            {
                Title = title;
                Width = 380;
                Height = 170;
                WindowStartupLocation = WindowStartupLocation.CenterOwner;
                ResizeMode = ResizeMode.NoResize;
                Background = Brushes.White;

                var root = new Grid { Margin = new Thickness(14) };
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                var txt = new TextBlock
                {
                    Text = prompt,
                    Foreground = Brushes.Black,
                    Margin = new Thickness(0, 0, 0, 10),
                    TextWrapping = TextWrapping.Wrap
                };
                Grid.SetRow(txt, 0);
                root.Children.Add(txt);

                _tb = new TextBox
                {
                    Text = defaultValue,
                    Margin = new Thickness(0, 0, 0, 12)
                };
                Grid.SetRow(_tb, 1);
                root.Children.Add(_tb);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right
                };

                var ok = new Button { Content = "確定", Width = 72, Margin = new Thickness(0, 0, 8, 0) };
                ok.Click += (_, __) =>
                {
                    _result = _tb.Text;
                    DialogResult = true;
                    Close();
                };

                var cancel = new Button { Content = "取消", Width = 72 };
                cancel.Click += (_, __) =>
                {
                    DialogResult = false;
                    Close();
                };

                btnPanel.Children.Add(ok);
                btnPanel.Children.Add(cancel);

                Grid.SetRow(btnPanel, 2);
                root.Children.Add(btnPanel);

                Content = root;

                Loaded += (_, __) =>
                {
                    _tb.Focus();
                    _tb.SelectAll();
                };
            }

            public static string? Show(Window owner, string title, string prompt, string defaultValue)
            {
                var dlg = new SimpleInputDialog(title, prompt, defaultValue) { Owner = owner };
                var ok = dlg.ShowDialog();
                if (ok == true) return dlg._result;
                return null;
            }
        }

        public void SyncAutoFlowTemplate(NodeControl node, string? currentTopText)
        {
            if (node == null)
                return;

            string text = currentTopText ?? "";
            const string placeholder = "{{input}}";

            if (text.Contains(placeholder, StringComparison.Ordinal))
            {
                _autoFlowTemplatesByNode[node.Id] = text;
            }
        }

        public string GetAutoFlowTemplate(NodeControl node)
        {
            if (node == null)
                return "";

            if (_autoFlowTemplatesByNode.TryGetValue(node.Id, out var stored) &&
                !string.IsNullOrWhiteSpace(stored))
            {
                return stored;
            }

            string current = node.GetTopText() ?? "";
            if (current.Contains("{{input}}", StringComparison.Ordinal))
            {
                _autoFlowTemplatesByNode[node.Id] = current;
                return current;
            }

            return "";
        }

        public string CurrentFileDisplayKey()
        {
            if (string.IsNullOrWhiteSpace(_currentFilePath))
                return "default";

            try
            {
                return System.IO.Path.GetFileNameWithoutExtension(_currentFilePath);
            }
            catch
            {
                return "default";
            }
        }

        public void ClearAutoFlowTemplate(NodeControl node)
        {
            if (node == null)
                return;

            _autoFlowTemplatesByNode.Remove(node.Id);
        }

        public NodeControl? GetFirstDownstreamNode(NodeControl node)
        {
            return GetDownstreamNodes(node).FirstOrDefault();
        }

        public bool TryPrepareAutoFlowInput(NodeControl fromNode, NodeControl toNode)
        {
            if (!TryBuildAutoFlowInjectedPrompt(fromNode, toNode, out var injected))
                return false;

            toNode.SetTopText(injected);
            toNode.RefreshModelSelectionUI();
            RefreshDecisionForNode(toNode);
            return true;
        }

        public bool TryBuildInputFromFirstUpstream(NodeControl node, out string injectedPrompt)
        {
            injectedPrompt = "";

            if (node == null)
                return false;

            var incoming = GetConnectionsForContext()
                .Where(c => ReferenceEquals(c.EndNode, node))
                .Select(c => c.StartNode)
                .Where(n => n != null)
                .FirstOrDefault();

            return incoming != null && TryBuildAutoFlowInjectedPrompt(incoming, node, out injectedPrompt);
        }

        private bool TryBuildAutoFlowInjectedPrompt(
            NodeControl fromNode,
            NodeControl toNode,
            out string injectedPrompt)
        {
            injectedPrompt = "";

            if (fromNode == null || toNode == null)
                return false;

            string sourceText = (fromNode.GetBottomText() ?? "").Trim();
            if (string.IsNullOrWhiteSpace(sourceText))
                return false;

            if (toNode.GetTopLocked())
                return false;

            string template = GetAutoFlowTemplate(toNode);
            if (string.IsNullOrWhiteSpace(template))
                return false;

            const string placeholder = "{{input}}";

            if (!template.Contains(placeholder, StringComparison.Ordinal))
                return false;

            string injected = template.Replace(placeholder, sourceText, StringComparison.Ordinal);

            if (string.IsNullOrWhiteSpace(injected))
                return false;

            injectedPrompt = injected;
            return true;
        }

        public bool TryPrepareInputFromFirstUpstream(NodeControl node)
        {
            if (node == null)
                return false;

            string topText = node.GetTopText() ?? "";
            if (!topText.Contains("{{input}}", StringComparison.Ordinal))
                return false;

            var incoming = GetConnectionsForContext()
                .Where(c => ReferenceEquals(c.EndNode, node))
                .Select(c => c.StartNode)
                .Where(n => n != null)
                .FirstOrDefault();

            return incoming != null && TryPrepareAutoFlowInput(incoming, node);
        }

        public void FocusDecisionNode(NodeControl node)
        {
            if (node == null)
                return;

            _lastDecisionNode = node;
            _hoveredDecisionNode = node;
            ShowDecisionForNode(node);
        }

        public NodeAutoFlowPolicy GetNodeAutoFlowPolicy(NodeControl node)
        {
            if (node == null)
                return NodeAutoFlowPolicy.Default;

            if (_autoFlowPoliciesByNode.TryGetValue(node.Id, out var policy))
                return policy ?? NodeAutoFlowPolicy.Default;

            _autoFlowPoliciesByNode[node.Id] = NodeAutoFlowPolicy.Default;
            return NodeAutoFlowPolicy.Default;
        }

        public void SetNodeAutoFlowPolicy(NodeControl node, NodeAutoFlowPolicy policy)
        {
            if (node == null)
                return;

            _autoFlowPoliciesByNode[node.Id] = policy ?? NodeAutoFlowPolicy.Default;
            SaveState();
        }

        public bool IsNodeAutoRunEnabled(NodeControl node)
        {
            return GetNodeAutoFlowPolicy(node).AutoRunEnabled;
        }

        public async Task RunManualWorkflowChainAsync(NodeControl startNode)
        {
            if (startNode == null || !MainCanvas.Children.Contains(startNode))
                return;

            var visited = new HashSet<Guid>();
            var current = startNode;

            for (int step = 0; step < 12; step++)
            {
                if (current == null || !MainCanvas.Children.Contains(current))
                    return;

                if (!visited.Add(current.Id))
                    return;

                FocusDecisionNode(current);

                if (ShouldStopBeforeUnsupportedDownstreamNode(current))
                {
                    current.SetBottomText(
                        "（此下游節點需要尚未接上的專用代理，已停止以避免不必要的 token 消耗。）\n" +
                        "目前可先使用前一個節點的結果；等 presentation/file/media/workflow agent 完成後再啟用此步。");
                    return;
                }

                bool success = await current.RunCurrentTopTextAsync();
                if (!success)
                    return;

                current = GetFirstDownstreamNode(current);
                if (current == null)
                    return;
            }
        }

        public void RunDryWorkflowChain(NodeControl startNode)
        {
            if (startNode == null || !MainCanvas.Children.Contains(startNode))
                return;

            var visited = new HashSet<Guid>();
            var current = startNode;
            string upstreamText = (startNode.GetBottomText() ?? "").Trim();

            for (int step = 0; step < 12; step++)
            {
                if (current == null || !MainCanvas.Children.Contains(current))
                    return;

                if (!visited.Add(current.Id))
                {
                    current.SetBottomText("（Dry run 停止：偵測到循環連線。）");
                    FocusDecisionNode(current);
                    return;
                }

                FocusDecisionNode(current);

                if (ShouldStopBeforeUnsupportedDownstreamNode(current))
                {
                    current.SetBottomText(
                        "（Dry run 停止：此節點需要尚未接上的專用代理。）\n" +
                        "真實執行時也會在此停止，以避免不必要的 token 消耗。");
                    return;
                }

                string label = ExtractWorkflowStepLabel(current.GetTopText());
                string inputSummary = string.IsNullOrWhiteSpace(upstreamText)
                    ? "無上游輸出或由本節點原始輸入開始。"
                    : SummarizeForDryRun(upstreamText);

                current.SetBottomText(
                    $"（Dry run，未呼叫模型）\n" +
                    $"Step: {label}\n" +
                    $"Input: {inputSummary}\n" +
                    $"Output: simulated result for downstream wiring test.");

                upstreamText = current.GetBottomText();
                current = GetFirstDownstreamNode(current);
                if (current == null)
                    return;
            }
        }

        private static string ExtractWorkflowStepLabel(string? topText)
        {
            string text = topText ?? "";
            foreach (var rawLine in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                var line = rawLine.Trim();
                if (line.StartsWith("Step:", StringComparison.OrdinalIgnoreCase))
                    return line.Substring("Step:".Length).Trim();
            }

            var first = text.Replace("\r\n", "\n").Replace('\r', '\n')
                .Split('\n')
                .Select(x => x.Trim())
                .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));

            if (string.IsNullOrWhiteSpace(first))
                return "workflow step";

            int pipeIndex = first.IndexOf('｜');
            return pipeIndex >= 0 && pipeIndex < first.Length - 1
                ? first.Substring(pipeIndex + 1).Trim()
                : first;
        }

        private static string SummarizeForDryRun(string text)
        {
            string normalized = Regex.Replace(text ?? "", @"\s+", " ").Trim();
            if (normalized.Length <= 90)
                return normalized;

            return normalized.Substring(0, 90) + "...";
        }

        private bool ShouldStopBeforeUnsupportedDownstreamNode(NodeControl node)
        {
            if (node != null && _unsupportedDownstreamNodeIds.Contains(node.Id))
                return true;

            string top = node?.GetTopText() ?? "";
            if (string.IsNullOrWhiteSpace(top))
                return false;

            bool isFutureTarget = top.Contains("(future target:", StringComparison.OrdinalIgnoreCase);
            if (!isFutureTarget)
                return false;

            return top.Contains("future target: presentation-agent", StringComparison.OrdinalIgnoreCase) ||
                   top.Contains("future target: file-agent", StringComparison.OrdinalIgnoreCase) ||
                   top.Contains("future target: media-agent", StringComparison.OrdinalIgnoreCase) ||
                   top.Contains("future target: workflow-agent", StringComparison.OrdinalIgnoreCase);
        }

        public IReadOnlyList<NodeControl> GetDownstreamNodes(NodeControl node)
        {
            if (node == null)
                return Array.Empty<NodeControl>();

            return GetConnectionsForContext()
                .Where(c => ReferenceEquals(c.StartNode, node))
                .Select(c => c.EndNode)
                .Where(n => n != null)
                .Distinct()
                .ToList();
        }
        internal static class MenuConfirmDialog
        {
            public static bool ShowDeleteConfirm(Window owner, string title, string message, FrameworkElement resourceHost)
            {
                var dlg = new MenuConfirmWindow(owner, title, message, resourceHost);
                return dlg.ShowDialog() == true;
            }

            private sealed class MenuConfirmWindow : Window
            {
                public MenuConfirmWindow(Window owner, string title, string message, FrameworkElement resourceHost)
                {
                    Owner = owner;
                    Title = title;

                    WindowStyle = WindowStyle.None;
                    ResizeMode = ResizeMode.NoResize;
                    AllowsTransparency = true;
                    Background = Brushes.Transparent;
                    ShowInTaskbar = false;
                    Topmost = true;
                    WindowStartupLocation = WindowStartupLocation.CenterOwner;

                    Width = 360;
                    Height = 170;

                    var bg = TryGetBrush(resourceHost, "FileMenuBg", "NodeMenuBg", Colors.White);
                    var border = TryGetBrush(resourceHost, "FileMenuBorder", "NodeMenuBorder", (Color)ColorConverter.ConvertFromString("#D6D6D6")!);
                    var text = TryGetBrush(resourceHost, "FileMenuText", "NodeMenuText", (Color)ColorConverter.ConvertFromString("#222222")!);

                    var outer = new Border
                    {
                        Background = bg,
                        BorderBrush = border,
                        BorderThickness = new Thickness(1),
                        CornerRadius = new CornerRadius(12),
                        Padding = new Thickness(12),
                        SnapsToDevicePixels = true
                    };

                    outer.MouseLeftButtonDown += (_, e) =>
                    {
                        if (e.ButtonState == MouseButtonState.Pressed)
                        {
                            try { DragMove(); } catch { }
                        }
                    };

                    var root = new Grid();
                    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                    root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                    root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                    var titleText = new TextBlock
                    {
                        Text = title,
                        Foreground = text,
                        FontSize = 13,
                        FontWeight = FontWeights.SemiBold,
                        Margin = new Thickness(0, 0, 0, 8)
                    };
                    Grid.SetRow(titleText, 0);
                    root.Children.Add(titleText);

                    var msgText = new TextBlock
                    {
                        Text = message,
                        Foreground = text,
                        FontSize = 13,
                        TextWrapping = TextWrapping.Wrap,
                        TextAlignment = TextAlignment.Center,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center
                    };
                    Grid.SetRow(msgText, 1);
                    root.Children.Add(msgText);

                    var btnPanel = new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Right,
                        Margin = new Thickness(0, 12, 0, 0)
                    };

                    var cancel = CreateMenuButton("取消", text);
                    cancel.IsCancel = true;
                    cancel.Margin = new Thickness(0, 0, 8, 0);
                    cancel.Click += (_, __) => { DialogResult = false; Close(); };

                    var del = CreateMenuButton("刪除", new SolidColorBrush((Color)ColorConverter.ConvertFromString("#D32F2F")!));
                    del.IsDefault = true;
                    del.Click += (_, __) => { DialogResult = true; Close(); };

                    btnPanel.Children.Add(cancel);
                    btnPanel.Children.Add(del);

                    Grid.SetRow(btnPanel, 2);
                    root.Children.Add(btnPanel);

                    outer.Child = root;
                    Content = outer;

                    PreviewKeyDown += (_, e) =>
                    {
                        if (e.Key == Key.Escape)
                        {
                            DialogResult = false;
                            Close();
                            e.Handled = true;
                        }
                        else if (e.Key == Key.Enter)
                        {
                            DialogResult = true;
                            Close();
                            e.Handled = true;
                        }
                    };
                }

                private static Button CreateMenuButton(string caption, Brush fg)
                {
                    var btn = new Button
                    {
                        Content = caption,
                        FontSize = 13,
                        Foreground = fg,
                        Background = Brushes.Transparent,
                        BorderThickness = new Thickness(0),
                        Padding = new Thickness(8, 6, 8, 6),
                        Cursor = Cursors.Hand
                    };

                    btn.MouseEnter += (_, __) => btn.Opacity = 0.85;
                    btn.MouseLeave += (_, __) => btn.Opacity = 1.0;
                    btn.PreviewMouseLeftButtonDown += (_, __) => btn.Opacity = 0.70;
                    btn.PreviewMouseLeftButtonUp += (_, __) => btn.Opacity = 0.85;

                    return btn;
                }

                public bool NodeAcceptsAutoFlowInput(NodeControl node)
                {
                    if (node == null)
                        return false;

                    if (node.GetTopLocked())
                        return false;

                    string template = node.GetTopText() ?? "";
                    if (string.IsNullOrWhiteSpace(template))
                        return false;

                    return template.Contains("{{input}}", StringComparison.Ordinal);
                }
                private static Brush TryGetBrush(FrameworkElement host, string key1, string key2, Color fallback)
                {
                    try
                    {
                        if (host.TryFindResource(key1) is Brush b1) return b1;
                        if (host.TryFindResource(key2) is Brush b2) return b2;
                        if (Application.Current?.TryFindResource(key1) is Brush b3) return b3;
                        if (Application.Current?.TryFindResource(key2) is Brush b4) return b4;
                    }
                    catch { }
                    return new SolidColorBrush(fallback);
                }
            }
        }
    }
}
