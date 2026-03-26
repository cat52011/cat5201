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

        private readonly List<Connection> _connections = new();
        public int GetNextZIndex() => ++_zIndexCounter;

        private bool _isPanning = false;
        private Point _lastMousePos;

        private static readonly Random _random = new();

        private string SavesDir => @"D:\desk\college\final\cat5201\file";
        private string AttachmentsRootDir => System.IO.Path.Combine(SavesDir, "_attachments");

        private string? _currentFilePath;
        private bool _hasStarted = false;
        private bool _suppressSave = false;

        private readonly Dictionary<Guid, List<AttachmentInfo>> _attachmentsByNode = new();
        private readonly Dictionary<Guid, string> _nodeModelsById = new();

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
            string? NodeModel = null
        );

        private record ConnState(string StartId, string EndId, string StartThumb, string EndThumb);

        private record AttachmentState(
            string NodeId,
            string FileName,
            string RelativePath,
            string MimeType,
            string Kind
        );

        private record AppState(
            DateTime CreatedAt,
            string? InitialNodeId,
            List<NodeState> Nodes,
            List<ConnState> Connections,
            List<AttachmentState> Attachments,
            bool FileNameLocked = false
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

        public string GetNodeSelectedModel(NodeControl node)
        {
            if (node == null)
                return AiModels.DefaultNodeModel;

            if (_nodeModelsById.TryGetValue(node.Id, out var model))
                return _aiRouter.NormalizeNodeModel(model);

            var fallback = AiModels.DefaultNodeModel;
            _nodeModelsById[node.Id] = fallback;
            return fallback;
        }

        public void SetNodeSelectedModel(NodeControl node, string model)
        {
            if (node == null) return;

            _nodeModelsById[node.Id] = _aiRouter.NormalizeNodeModel(model);
            SaveState();
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

            _fileNameLockedByUser = false;
            _lastAppliedAutoKeyword = "";
            _lastInitialTopSnapshot = "";

            _attachmentsByNode.Clear();
            _nodeModelsById.Clear();

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

                _nodeModelsById[node.Id] = "gpt-5.4";
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

            _editingNode = null;
            _editingReason = EditReason.None;
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
                _nodeModelsById[node.Id] = "gpt-5.4";

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
            var fe = (FrameworkElement)node.FindName(thumbName)!;
            return fe.TranslatePoint(new Point(fe.RenderSize.Width / 2, fe.RenderSize.Height / 2), MainCanvas);
        }

        public void AddNode(double x, double y)
        {
            var node = new NodeControl();
            Canvas.SetLeft(node, SafeFinite(x - node.Width / 2, 0));
            Canvas.SetTop(node, SafeFinite(y - node.Height / 2, 0));
            Canvas.SetZIndex(node, GetNextZIndex());
            MainCanvas.Children.Add(node);
            HookNode(node);

            _nodeModelsById[node.Id] = AiModels.DefaultNodeModel;

            RequestBeginEdit(node, EditReason.NewNode);

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
                    GetNodeSelectedModel(child)
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

            var state = new AppState(
                DateTime.Now,
                _initialNode?.Id.ToString(),
                nodes,
                conns,
                atts,
                FileNameLocked: _fileNameLockedByUser
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
            _lastAppliedAutoKeyword = "";
            _lastInitialTopSnapshot = "";

            _attachmentsByNode.Clear();
            _nodeModelsById.Clear();

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

            _editingNode = null;
            _editingReason = EditReason.None;

            _suppressSave = true;
            try
            {
                ClearAll();

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

                    _nodeModelsById[node.Id] = _aiRouter.NormalizeNodeModel(n.NodeModel);

                    node.SetTopText(n.TopText ?? "");
                    node.SetBottomText(n.BottomText ?? "");
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
            }
        }

        private void ClearAll()
        {
            MainCanvas.Children.Clear();
            _connections.Clear();
            _zIndexCounter = 0;
            _initialNode = null;
            _nodeModelsById.Clear();

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

            string model = GetNodeSelectedModel(node);

            string instructions = "你是一個善於替筆記自動命名的助手。";

            string user =
$@"請將下面內容，取一個像 ChatGPT 自動命名筆記那樣的「短標題/關鍵字」：
- 使用繁體中文
- 盡量 6~16 字
- 只輸出標題本身，不要加引號、不要加編號、不要加任何解釋
- 不要包含檔案副檔名

內容：
{Truncate(topText.Trim(), 800)}";

            if (_aiRouter.IsPerplexitySonarModel(model))
            {
                var svc = _aiRouter.GetPerplexitySonarService(_aiRouter.MapPerplexitySonarModel(model));
                var text = await svc.GenerateAsync(
                    instructions,
                    user,
                    maxOutputTokens: 200,
                    ct: ct);

                return (text ?? "").Trim();
            }
            else if (_aiRouter.IsClaudeModel(model))
            {
                var text = await _aiRouter.GetClaudeService(model).GenerateAsync(instructions, user, maxOutputTokens: 200, ct: ct);
                return (text ?? "").Trim();
            }
            else
            {
                var text = await _aiRouter.GetOpenAiService(model).GenerateAsync(instructions, user, maxOutputTokens: 200, ct: ct);
                return (text ?? "").Trim();
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
                ClearEditingIfDeleted(n);
                MainCanvas.Children.Remove(n);
                _attachmentsByNode.Remove(n.Id);
                _nodeModelsById.Remove(n.Id);
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