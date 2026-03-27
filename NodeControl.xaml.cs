using Microsoft.Win32;
using System;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;

namespace test
{
    public partial class NodeControl : UserControl
    {
        private Path? _tempPath;
        private Point _startPoint;
        private MainWindow? _parent;

        private bool _isDraggingNode = false;
        private Point _dragStartOnCanvas;
        private Point _nodeStartPos;

        private bool _isTopLocked = false;
        private bool _isGenerating = false;

        private double _fontSize = 20;

        private bool _isSyncingModelSelector = false;
        private bool _modelsLoaded = false;

        public event EventHandler? Moved;
        public event EventHandler? ContentChanged;

        public Guid Id { get; }

        private readonly ObservableCollection<AttachmentVm> _attachments = new();

        private bool _isAttachmentMouseDown = false;
        private bool _isAttachmentDragging = false;
        private Point _attachDragStart;
        private double _attachScrollStartX;
        private const double AttachmentDragThreshold = 4.0;

        private sealed class AttachmentVm
        {
            public string FileName { get; set; } = "";
            public string RelativePath { get; set; } = "";
            public string Kind { get; set; } = "file";
            public string KindGlyph => Kind == "image" ? "🖼" : "📄";
        }

        public NodeControl() : this(Guid.NewGuid().ToString()) { }

        public NodeControl(string idString)
        {
            InitializeComponent();

            AttachmentItems.ItemsSource = _attachments;

            Loaded += (s, e) =>
            {
                _parent = Window.GetWindow(this) as MainWindow;
                EnsureModelSelectorLoaded();
                ApplyFontSize(_fontSize);
                RefreshAttachmentsUI();
                SyncModelSelectorFromParent();
                UpdateEditButtons();
            };

            if (!Guid.TryParse(idString, out var gid))
                gid = Guid.NewGuid();
            Id = gid;

            TopEditor.LostKeyboardFocus -= TopEditor_LostKeyboardFocus;
            TopEditor.KeyDown -= TopEditor_KeyDown;

            TopEditor.TextChanged += TopEditor_TextChanged;
            TopEditor.PreviewMouseLeftButtonDown += TopEditor_PreviewMouseLeftButtonDown;
        }

        public bool IsEditing => TopEditor != null && TopEditor.IsReadOnly == false;

        internal void EnterEditMode()
        {
            _isTopLocked = false;

            TopEditor.IsReadOnly = false;
            TopEditor.Focus();
            TopEditor.CaretIndex = TopEditor.Text?.Length ?? 0;

            EnsureModelSelectorLoaded();
            SyncModelSelectorFromParent();
            UpdateEditButtons();
        }

        internal void ForceExitEditMode()
        {
            TopEditor.IsReadOnly = true;
            UpdateEditButtons();
        }

        internal void EndEditBecauseSent()
        {
            TopEditor.IsReadOnly = true;
            UpdateEditButtons();
            _parent?.NotifyEditEnded(this);
        }

        private void EnsureModelSelectorLoaded()
        {
            if (_modelsLoaded || ModelSelector == null)
                return;

            LoadModelsFromRegistry();
            _modelsLoaded = true;
        }

        private void LoadModelsFromRegistry()
        {
            if (ModelSelector == null)
                return;

            string currentSelectedId = GetSelectedModelIdFromComboBox();

            _isSyncingModelSelector = true;
            try
            {
                ModelSelector.ItemsSource = null;
                ModelSelector.ItemsSource = AiModelRegistry.All;
            }
            finally
            {
                _isSyncingModelSelector = false;
            }

            SelectModelInComboBox(currentSelectedId);
        }

        private string GetSelectedModelIdFromComboBox()
        {
            if (ModelSelector?.SelectedItem is AiModelDefinition model &&
                !string.IsNullOrWhiteSpace(model.Id))
            {
                return model.Id.Trim();
            }

            return AiModels.DefaultNodeModel;
        }

        private void SelectModelInComboBox(string modelId)
        {
            if (ModelSelector == null)
                return;

            modelId = AiModelHelper.NormalizeNodeModel(modelId);

            _isSyncingModelSelector = true;
            try
            {
                var match = AiModelRegistry.All.FirstOrDefault(x =>
                    string.Equals(x.Id, modelId, StringComparison.OrdinalIgnoreCase));

                if (match != null)
                {
                    ModelSelector.SelectedItem = match;
                    return;
                }

                if (AiModelRegistry.All.Count > 0)
                    ModelSelector.SelectedItem = AiModelRegistry.All[0];
            }
            finally
            {
                _isSyncingModelSelector = false;
            }
        }

        private void UpdateEditButtons()
        {
            bool editable = (TopEditor != null && TopEditor.IsReadOnly == false);

            if (PlusButton != null)
            {
                PlusButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
                PlusButton.IsEnabled = editable;
            }

            if (SendButton != null)
            {
                SendButton.Visibility = editable ? Visibility.Visible : Visibility.Collapsed;
                SendButton.IsEnabled = editable && !_isGenerating;
            }

            if (ModelSelector != null)
            {
                ModelSelector.Visibility = Visibility.Visible;
                ModelSelector.IsEnabled = editable && !_isGenerating;
            }

            RefreshAttachmentsUI();
        }

        private void SyncModelSelectorFromParent()
        {
            EnsureModelSelectorLoaded();

            if (_parent == null || ModelSelector == null)
                return;

            var model = _parent.GetNodeSelectedModel(this);
            SelectModelInComboBox(model);
        }

        private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingModelSelector) return;
            if (_parent == null) return;
            if (ModelSelector.SelectedItem is not AiModelDefinition model) return;
            if (string.IsNullOrWhiteSpace(model.Id)) return;

            _parent.SetNodeSelectedModel(this, model.Id);
        }

        public bool GetTopLocked() => _isTopLocked;

        public void SetTopLocked(bool locked)
        {
            _isTopLocked = locked;
            TopEditor.IsReadOnly = true;
            UpdateEditButtons();
        }

        public double GetFontSize() => _fontSize;

        public void SetFontSize(double size)
        {
            if (size < 5) size = 5;
            if (size > 200) size = 200;

            _fontSize = size;
            ApplyFontSize(_fontSize);
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void ApplyFontSize(double size)
        {
            if (TopEditor != null) TopEditor.FontSize = size;
            if (BottomDisplay != null) BottomDisplay.FontSize = size;
        }

        internal void RefreshAttachmentsUI()
        {
            if (_parent == null)
            {
                _attachments.Clear();

                if (AttachmentListHost != null)
                    AttachmentListHost.Visibility = Visibility.Collapsed;

                if (ModelSelector != null &&
                    AttachmentColumn != null &&
                    AttachmentSplitterColumn != null &&
                    ModelColumn != null)
                {
                    AttachmentColumn.Width = new GridLength(0);
                    AttachmentSplitterColumn.Width = new GridLength(0);
                    ModelColumn.Width = new GridLength(1, GridUnitType.Star);

                    Grid.SetColumn(ModelSelector, 0);
                    Grid.SetColumnSpan(ModelSelector, 3);
                }

                return;
            }

            var list = _parent.GetAttachmentsForNode(this);

            _attachments.Clear();
            foreach (var a in list)
            {
                _attachments.Add(new AttachmentVm
                {
                    FileName = a.FileName,
                    RelativePath = a.RelativePath,
                    Kind = a.Kind
                });
            }

            if (AttachmentListHost != null &&
                ModelSelector != null &&
                AttachmentColumn != null &&
                AttachmentSplitterColumn != null &&
                ModelColumn != null)
            {
                bool hasAttachments = _attachments.Count > 0;

                if (hasAttachments)
                {
                    AttachmentListHost.Visibility = Visibility.Visible;

                    AttachmentColumn.Width = new GridLength(68, GridUnitType.Star);
                    AttachmentSplitterColumn.Width = new GridLength(2);
                    ModelColumn.Width = new GridLength(32, GridUnitType.Star);

                    Grid.SetColumn(ModelSelector, 2);
                    Grid.SetColumnSpan(ModelSelector, 1);
                }
                else
                {
                    AttachmentListHost.Visibility = Visibility.Collapsed;

                    AttachmentColumn.Width = new GridLength(0);
                    AttachmentSplitterColumn.Width = new GridLength(0);
                    ModelColumn.Width = new GridLength(1, GridUnitType.Star);

                    Grid.SetColumn(ModelSelector, 0);
                    Grid.SetColumnSpan(ModelSelector, 3);
                }
            }

            if (AttachmentScroll != null)
                AttachmentScroll.ScrollToHorizontalOffset(0);
        }

        private void AttachmentItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent == null) return;
            if (_isAttachmentDragging) return;

            if (e.ClickCount == 2)
            {
                if (sender is FrameworkElement fe && fe.Tag is AttachmentVm vm)
                {
                    _parent.OpenAttachment(vm.RelativePath);
                    e.Handled = true;
                }
            }
        }

        private void AttachmentDelete_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;

            if (sender is Button btn && btn.Tag is AttachmentVm vm)
            {
                bool ok = MainWindow.MenuConfirmDialog.ShowDeleteConfirm(
                    owner: _parent,
                    title: "刪除確認",
                    message: $"確定要刪除附件？\n{vm.FileName}",
                    resourceHost: _parent);

                if (!ok) return;

                _parent.RemoveAttachment(this, vm.RelativePath);
                RefreshAttachmentsUI();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private void AttachmentScroll_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (AttachmentScroll == null) return;

            _isAttachmentMouseDown = true;
            _isAttachmentDragging = false;
            _attachDragStart = e.GetPosition(AttachmentScroll);
            _attachScrollStartX = AttachmentScroll.HorizontalOffset;
        }

        private void AttachmentScroll_PreviewMouseMove(object sender, MouseEventArgs e)
        {
            if (!_isAttachmentMouseDown) return;
            if (AttachmentScroll == null) return;
            if (e.LeftButton != MouseButtonState.Pressed) return;

            var cur = e.GetPosition(AttachmentScroll);
            var dx = cur.X - _attachDragStart.X;

            if (!_isAttachmentDragging)
            {
                if (Math.Abs(dx) < AttachmentDragThreshold) return;

                _isAttachmentDragging = true;
                AttachmentScroll.CaptureMouse();
                e.Handled = true;
            }

            AttachmentScroll.ScrollToHorizontalOffset(_attachScrollStartX - dx);
            e.Handled = true;
        }

        private void AttachmentScroll_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (AttachmentScroll == null) return;

            _isAttachmentMouseDown = false;

            if (_isAttachmentDragging)
            {
                _isAttachmentDragging = false;
                AttachmentScroll.ReleaseMouseCapture();
                e.Handled = true;
            }
        }

        private void ContextMenu_Opened(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;
            DeleteMenuItem.IsEnabled = !_parent.IsInitialNode(this);
            EditMenuItem.IsEnabled = true;
            FontSizeMenuItem.IsEnabled = true;
        }

        private void EditMenuItem_Click(object sender, RoutedEventArgs e)
        {
            _parent?.RequestBeginEdit(this, MainWindow.EditReason.UserEdit);
        }

        private void FontSizeMenuItem_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new FontSizeSliderDialog(
                owner: Window.GetWindow(this),
                title: "調整字體大小",
                initialValue: _fontSize,
                min: 5,
                max: 200,
                onPreviewValueChanged: v => ApplyFontSize(v)
            );

            bool? ok = dlg.ShowDialog();
            if (ok == true) SetFontSize(dlg.SelectedValue);
            else ApplyFontSize(_fontSize);
        }

        private void DeleteMenuItem_Click(object sender, RoutedEventArgs e)
        {
            if (_parent == null) return;
            if (_parent.IsInitialNode(this)) return;

            if (_parent.HasOutgoingConnections(this))
            {
                var ok = MainWindow.MenuConfirmDialog.ShowDeleteConfirm(
                    owner: _parent,
                    title: "確認刪除",
                    message: "當前區塊不是末端區塊，是否確認刪除？",
                    resourceHost: _parent);

                if (!ok) return;
            }

            _parent.DeleteNodeAndDescendants(this);
        }

        private void TopEditor_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (string.IsNullOrWhiteSpace(TopEditor.Text))
                _isTopLocked = false;

            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        private void TopEditor_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (!IsEditing)
            {
                e.Handled = true;
            }
        }

        private void TopEditor_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) { }
        private void TopEditor_KeyDown(object sender, KeyEventArgs e) { }

        private void PlusButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsEditing) return;

            if (_parent == null)
            {
                MessageBox.Show("（找不到 MainWindow，無法上傳）", "錯誤", MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            var dlg = new OpenFileDialog
            {
                Title = "選擇要上傳給 AI 的檔案/照片",
                Multiselect = true,
                Filter =
                    "所有檔案 (*.*)|*.*|" +
                    "圖片 (*.png;*.jpg;*.jpeg;*.webp;*.bmp)|*.png;*.jpg;*.jpeg;*.webp;*.bmp|" +
                    "文件 (*.pdf;*.txt;*.md;*.csv;*.json)|*.pdf;*.txt;*.md;*.csv;*.json"
            };

            if (dlg.ShowDialog(Window.GetWindow(this)) == true)
            {
                _parent.AddAttachmentsForNode(this, dlg.FileNames);
                RefreshAttachmentsUI();
                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        private async void SendButton_Click(object sender, RoutedEventArgs e)
        {
            if (!IsEditing) return;
            if (_isGenerating) return;

            var top = TopEditor.Text ?? "";
            if (string.IsNullOrWhiteSpace(top)) return;

            _isTopLocked = true;
            EndEditBecauseSent();

            ContentChanged?.Invoke(this, EventArgs.Empty);
            _parent?.NotifyNodeSubmitted(this);

            await GenerateBottomReplyFromTopAsync(top);
        }

        private async Task GenerateBottomReplyFromTopAsync(string topText)
        {
            _isGenerating = true;
            UpdateEditButtons();

            try
            {
                BottomDisplay.Text = "";

                if (_parent == null)
                {
                    BottomDisplay.Text = "（找不到 MainWindow，無法呼叫 AI）";
                    return;
                }

                if (_parent.NodeService == null)
                {
                    BottomDisplay.Text = "（NodeService 尚未初始化）";
                    return;
                }

                using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(3));

                string finalReply = await _parent.NodeService.GenerateStreamAsync(
                    this,
                    topText,
                    delta =>
                    {
                        Dispatcher.Invoke(() =>
                        {
                            BottomDisplay.AppendText(delta);
                            BottomDisplay.ScrollToEnd();
                        });
                    },
                    cts.Token);

                if (string.IsNullOrWhiteSpace(finalReply))
                {
                    BottomDisplay.Text = "（AI 沒有回傳文字）";
                }
                else
                {
                    if (!string.Equals(BottomDisplay.Text, finalReply, StringComparison.Ordinal))
                    {
                        BottomDisplay.Text = finalReply.Trim();
                    }
                }

                ContentChanged?.Invoke(this, EventArgs.Empty);
            }
            catch (Exception ex)
            {
                BottomDisplay.Text = $"（AI 產生失敗）\n{ex.Message}";
            }
            finally
            {
                _isGenerating = false;
                UpdateEditButtons();
            }
        }

        public string GetTopText() => TopEditor.Text ?? "";
        public void SetTopText(string text) => TopEditor.Text = text ?? "";
        public string GetBottomText() => BottomDisplay.Text ?? "";
        public void SetBottomText(string text) => BottomDisplay.Text = text ?? "";

        private sealed class FontSizeSliderDialog : Window
        {
            private readonly Slider _slider;
            private readonly TextBlock _valueText;

            public double SelectedValue => _slider.Value;

            public FontSizeSliderDialog(Window? owner, string title, double initialValue, double min, double max, Action<double> onPreviewValueChanged)
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

                Width = 520;
                Height = 220;

                var bg = TryGetBrush("NodeMenuBg", Colors.White);
                var border = TryGetBrush("NodeMenuBorder", (Color)ColorConverter.ConvertFromString("#D6D6D6")!);
                var text = TryGetBrush("NodeMenuText", (Color)ColorConverter.ConvertFromString("#222222")!);

                var outer = new Border
                {
                    Background = bg,
                    BorderBrush = border,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(16),
                    Padding = new Thickness(18),
                    SnapsToDevicePixels = true
                };

                Content = outer;

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
                    Margin = new Thickness(0, 0, 0, 10)
                };
                Grid.SetRow(titleText, 0);
                root.Children.Add(titleText);

                var centerPanel = new Grid
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                centerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                centerPanel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

                _valueText = new TextBlock
                {
                    Foreground = text,
                    FontSize = 14,
                    Text = ((int)Math.Round(initialValue)).ToString(CultureInfo.InvariantCulture),
                    Margin = new Thickness(0, 0, 0, 10),
                    HorizontalAlignment = HorizontalAlignment.Left
                };
                Grid.SetRow(_valueText, 0);
                centerPanel.Children.Add(_valueText);

                _slider = new Slider
                {
                    Minimum = min,
                    Maximum = max,
                    Value = Math.Max(min, Math.Min(max, initialValue)),
                    TickFrequency = 1,
                    IsSnapToTickEnabled = false,
                    AutoToolTipPlacement = AutoToolTipPlacement.None,
                    Margin = new Thickness(0, 0, 0, 6)
                };
                _slider.ValueChanged += (_, __) =>
                {
                    var value = _slider.Value;
                    _valueText.Text = ((int)Math.Round(value)).ToString(CultureInfo.InvariantCulture);
                    onPreviewValueChanged?.Invoke(value);
                };
                Grid.SetRow(_slider, 1);
                centerPanel.Children.Add(_slider);

                Grid.SetRow(centerPanel, 1);
                root.Children.Add(centerPanel);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 12, 0, 0)
                };

                var cancel = CreateDialogButton("取消", text);
                cancel.IsCancel = true;
                cancel.Margin = new Thickness(0, 0, 8, 0);
                cancel.Click += (_, __) =>
                {
                    DialogResult = false;
                    Close();
                };

                var ok = CreateDialogButton("確定", text);
                ok.IsDefault = true;
                ok.Click += (_, __) =>
                {
                    DialogResult = true;
                    Close();
                };

                btnPanel.Children.Add(cancel);
                btnPanel.Children.Add(ok);

                Grid.SetRow(btnPanel, 2);
                root.Children.Add(btnPanel);

                outer.Child = root;

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

                Loaded += (_, __) => _slider.Focus();
            }

            private static Button CreateDialogButton(string caption, Brush fg)
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

            private static Brush TryGetBrush(string key, Color fallback)
            {
                try
                {
                    if (Application.Current?.TryFindResource(key) is Brush b)
                        return b;
                }
                catch { }

                return new SolidColorBrush(fallback);
            }
        }

        private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent == null) return;

            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null)
                return;

            _isDraggingNode = true;
            _dragStartOnCanvas = e.GetPosition(mw.MainCanvas);
            _nodeStartPos = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
            CaptureMouse();

            Panel.SetZIndex(this, _parent.GetNextZIndex());
            e.Handled = true;
        }

        private void DragHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingNode || _parent == null) return;

            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null)
                return;

            var current = e.GetPosition(mw.MainCanvas);
            var offset = current - _dragStartOnCanvas;

            Canvas.SetLeft(this, _nodeStartPos.X + offset.X);
            Canvas.SetTop(this, _nodeStartPos.Y + offset.Y);

            Moved?.Invoke(this, EventArgs.Empty);
        }

        private void DragHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingNode) return;
            _isDraggingNode = false;
            ReleaseMouseCapture();
            e.Handled = true;
        }

        private void Corner_DragStarted(object sender, DragStartedEventArgs e)
        {
            if (_parent == null) return;
            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null) return;
            if (sender is not Thumb thumb) return;

            var center = GetThumbCenterOnCanvas(thumb, mw.MainCanvas);
            _startPoint = center;

            _tempPath = new Path
            {
                Stroke = Brushes.DimGray,
                StrokeThickness = 3,
                StrokeDashArray = new DoubleCollection { 4, 4 },
                IsHitTestVisible = false
            };

            var geo = new PathGeometry();
            var fig = new PathFigure { StartPoint = center };
            fig.Segments.Add(new BezierSegment(center, center, center, true));
            geo.Figures.Add(fig);
            _tempPath.Data = geo;

            Canvas.SetZIndex(_tempPath, _parent.GetNextZIndex());
            mw.MainCanvas.Children.Add(_tempPath);
        }

        private void Corner_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_tempPath?.Data is not PathGeometry geo) return;
            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null) return;

            Point current = Mouse.GetPosition(mw.MainCanvas);

            if (geo.Figures.Count == 0) return;
            if (geo.Figures[0].Segments.Count == 0) return;
            if (geo.Figures[0].Segments[0] is not BezierSegment seg) return;

            seg.Point1 = new Point((_startPoint.X + current.X) / 2, _startPoint.Y);
            seg.Point2 = new Point((_startPoint.X + current.X) / 2, current.Y);
            seg.Point3 = current;
        }

        private void Corner_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_parent == null) return;
            if (Window.GetWindow(this) is not MainWindow mw || mw.MainCanvas == null) return;
            if (sender is not Thumb thumb) return;

            if (_tempPath != null)
            {
                mw.MainCanvas.Children.Remove(_tempPath);
                _tempPath = null;
            }

            Point current = Mouse.GetPosition(mw.MainCanvas);

            var newNode = new NodeControl();
            Canvas.SetLeft(newNode, current.X - newNode.Width / 2);
            Canvas.SetTop(newNode, current.Y - newNode.Height / 2);
            Canvas.SetZIndex(newNode, _parent.GetNextZIndex());
            mw.MainCanvas.Children.Add(newNode);
            _parent.HookNode(newNode);

            string targetThumb = thumb.Name == "ThumbTL" ? "ThumbTR" : "ThumbTL";
            _parent.CreateCurve(this, thumb.Name, newNode, targetThumb);

            _parent.RequestBeginEdit(newNode, MainWindow.EditReason.NewNode);
        }

        private static Point GetThumbCenterOnCanvas(FrameworkElement thumb, Canvas canvas)
        {
            return thumb.TranslatePoint(
                new Point(thumb.ActualWidth / 2, thumb.ActualHeight / 2),
                canvas);
        }

        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (sender is not Thumb thumb)
                return;

            if (thumb.Name == "ThumbBL")
            {
                ThumbBL_DragDelta(sender, e);
                return;
            }

            if (thumb.Name == "ThumbBR")
            {
                ThumbBR_DragDelta(sender, e);
                return;
            }
        }

        private void ThumbBL_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = Width - e.HorizontalChange;
            double newHeight = Height + e.VerticalChange;

            if (newWidth >= 150)
            {
                double left = Canvas.GetLeft(this);
                Canvas.SetLeft(this, left + e.HorizontalChange);
                Width = newWidth;
            }

            if (newHeight >= 200)
                Height = newHeight;

            Moved?.Invoke(this, EventArgs.Empty);
        }

        private void ThumbBR_DragDelta(object sender, DragDeltaEventArgs e)
        {
            double newWidth = Width + e.HorizontalChange;
            double newHeight = Height + e.VerticalChange;

            if (newWidth >= 150)
                Width = newWidth;

            if (newHeight >= 200)
                Height = newHeight;

            Moved?.Invoke(this, EventArgs.Empty);
        }
    }

    public class BottomOffsetConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double height = (double)value;
            double offset = parameter != null ? double.Parse(parameter.ToString()!) : 0;
            return height - offset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    public class RightOffsetConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = (double)value;
            double offset = parameter != null ? double.Parse(parameter.ToString()!) : 0;
            return width - offset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    public class HeaderLeftConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double offset = parameter != null ? double.Parse(parameter.ToString()!) : 0;
            return offset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    public class HeaderWidthConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            double width = (double)value;
            double offset = parameter != null ? double.Parse(parameter.ToString()!) : 0;
            return width - offset;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture) => null!;
    }

    public class AttachmentPanelMarginConverter : IMultiValueConverter
    {
        public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
        {
            try
            {
                double btnH = values.Length > 0 && values[0] is double d0 ? d0 : 40.0;
                Thickness btnM = values.Length > 1 && values[1] is Thickness t ? t : new Thickness(0, 0, 0, 10);
                double panelH = values.Length > 2 && values[2] is double d2 ? d2 : 28.0;

                double lr = 70.0;
                if (parameter != null && double.TryParse(parameter.ToString(), NumberStyles.Any, CultureInfo.InvariantCulture, out var p))
                    lr = p;

                double targetCenterFromBottom = btnM.Bottom + (btnH / 2.0);
                double bottom = targetCenterFromBottom - (panelH / 2.0);

                if (double.IsNaN(bottom) || double.IsInfinity(bottom)) bottom = 16.0;
                if (bottom < 0) bottom = 0;

                return new Thickness(lr, 0, lr, bottom);
            }
            catch
            {
                return new Thickness(66, 0, 66, 16);
            }
        }

        public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}