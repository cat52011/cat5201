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
using System.Windows.Threading;

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
            if (_parent == null || ModelSelector == null)
                return;

            var model = _parent.GetNodeSelectedModel(this);

            _isSyncingModelSelector = true;
            try
            {
                ComboBoxItem? target = null;

                foreach (var item in ModelSelector.Items)
                {
                    if (item is ComboBoxItem cbi &&
                        cbi.Tag is string tag &&
                        string.Equals(tag, model, StringComparison.OrdinalIgnoreCase))
                    {
                        target = cbi;
                        break;
                    }
                }

                if (target != null)
                    ModelSelector.SelectedItem = target;
                else
                    ModelSelector.SelectedIndex = 0;
            }
            finally
            {
                _isSyncingModelSelector = false;
            }
        }

        private void ModelSelector_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isSyncingModelSelector) return;
            if (_parent == null) return;
            if (ModelSelector.SelectedItem is not ComboBoxItem item) return;
            if (item.Tag is not string model || string.IsNullOrWhiteSpace(model)) return;

            _parent.SetNodeSelectedModel(this, model);
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

        private void DragHeader_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_parent == null) return;
            _isDraggingNode = true;
            DragHeader.CaptureMouse();
            _dragStartOnCanvas = e.GetPosition(_parent.MainCanvas);
            _nodeStartPos = new Point(Canvas.GetLeft(this), Canvas.GetTop(this));
        }

        private void DragHeader_MouseMove(object sender, MouseEventArgs e)
        {
            if (!_isDraggingNode || _parent == null) return;
            var cur = e.GetPosition(_parent.MainCanvas);
            var delta = cur - _dragStartOnCanvas;
            Canvas.SetLeft(this, _nodeStartPos.X + delta.X);
            Canvas.SetTop(this, _nodeStartPos.Y + delta.Y);
            Moved?.Invoke(this, EventArgs.Empty);
        }

        private void DragHeader_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_isDraggingNode) return;
            _isDraggingNode = false;
            DragHeader.ReleaseMouseCapture();
            Moved?.Invoke(this, EventArgs.Empty);
        }

        private void Corner_DragStarted(object sender, DragStartedEventArgs e)
        {
            var thumb = (Thumb)sender;
            _startPoint = thumb.TranslatePoint(new Point(thumb.Width / 2, thumb.Height / 2), _parent!.MainCanvas);

            _tempPath = new Path
            {
                Stroke = Brushes.Gray,
                StrokeThickness = 1,
                StrokeDashArray = new DoubleCollection { 4 },
                StrokeLineJoin = PenLineJoin.Round
            };
            _parent.MainCanvas.Children.Add(_tempPath);
            Canvas.SetZIndex(_tempPath, int.MaxValue);
        }

        private void Corner_DragDelta(object sender, DragDeltaEventArgs e)
        {
            if (_tempPath == null) return;
            var current = Mouse.GetPosition(_parent!.MainCanvas);
            var geom = new PathGeometry();
            var figure = new PathFigure { StartPoint = _startPoint };
            var ctrl1 = new Point((_startPoint.X + current.X) / 2, _startPoint.Y);
            var ctrl2 = new Point((_startPoint.X + current.X) / 2, current.Y);
            figure.Segments.Add(new BezierSegment(ctrl1, ctrl2, current, true));
            geom.Figures.Add(figure);
            _tempPath.Data = geom;
        }

        private void Corner_DragCompleted(object sender, DragCompletedEventArgs e)
        {
            if (_tempPath != null)
            {
                _parent!.MainCanvas.Children.Remove(_tempPath);
                _tempPath = null;
            }

            var thumb = (Thumb)sender;
            var end = Mouse.GetPosition(_parent!.MainCanvas);

            var newNode = new NodeControl();
            _parent!.MainCanvas.Children.Add(newNode);

            newNode.SetFontSize(this._fontSize);

            string inheritedModel = _parent.GetNodeSelectedModel(this);
            _parent.SetNodeSelectedModel(newNode, inheritedModel);

            string targetThumbName = thumb.Name == "ThumbTL" ? "ThumbTR" : "ThumbTL";
            Point offset = GetThumbCenterOffset(newNode, targetThumbName);

            Canvas.SetLeft(newNode, end.X - offset.X);
            Canvas.SetTop(newNode, end.Y - offset.Y);
            Canvas.SetZIndex(newNode, _parent.GetNextZIndex());
            _parent.HookNode(newNode);

            _parent.Dispatcher.InvokeAsync(() =>
            {
                _parent.CreateCurve(this, thumb.Name, newNode, targetThumbName);
                newNode.Moved?.Invoke(newNode, EventArgs.Empty);
                this.Moved?.Invoke(this, EventArgs.Empty);
                _parent.RequestBeginEdit(newNode, MainWindow.EditReason.NewNode);
            }, DispatcherPriority.Loaded);
        }

        private void ResizeThumb_DragDelta(object sender, DragDeltaEventArgs e)
        {
            var thumb = (Thumb)sender;

            double newWidth = this.Width;
            double newHeight = this.Height;
            double left = Canvas.GetLeft(this);

            if (thumb == ThumbBR)
            {
                newWidth += e.HorizontalChange;
                newHeight += e.VerticalChange;
            }
            else if (thumb == ThumbBL)
            {
                newWidth -= e.HorizontalChange;
                newHeight += e.VerticalChange;

                if (newWidth > 150)
                    left += e.HorizontalChange;
            }

            if (newWidth < 150) newWidth = 150;
            if (newHeight < 200) newHeight = 200;

            if (thumb == ThumbBL && this.Width != newWidth)
                Canvas.SetLeft(this, left);

            this.Width = newWidth;
            this.Height = newHeight;

            Moved?.Invoke(this, EventArgs.Empty);
        }

        private Point GetThumbCenterOffset(NodeControl node, string thumbName)
        {
            if (thumbName == "ThumbTL")
                return new Point(10 + ThumbTL.Width / 2, 10 + ThumbTL.Height / 2);

            if (thumbName == "ThumbTR")
                return new Point(270 + ThumbTR.Width / 2, 10 + ThumbTR.Height / 2);

            return new Point(0, 0);
        }

        public string GetTopText() => TopEditor.Text ?? "";

        public void SetTopText(string text)
        {
            TopEditor.Text = text ?? "";
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

        public string GetBottomText() => BottomDisplay.Text ?? "";

        public void SetBottomText(string text)
        {
            BottomDisplay.Text = text ?? "";
            ContentChanged?.Invoke(this, EventArgs.Empty);
        }

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
                    IsSnapToTickEnabled = true,
                    SmallChange = 1,
                    LargeChange = 1,
                    Width = 420,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                };

                _slider.ValueChanged += (_, __) =>
                {
                    var v = (int)Math.Round(_slider.Value);
                    _valueText.Text = v.ToString(CultureInfo.InvariantCulture);
                    onPreviewValueChanged(v);
                };

                Grid.SetRow(_slider, 1);
                centerPanel.Children.Add(_slider);

                Grid.SetRow(centerPanel, 1);
                root.Children.Add(centerPanel);

                var btnPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    Margin = new Thickness(0, 14, 0, 0)
                };

                var btnOk = CreateMenuButton("確定", text);
                btnOk.IsDefault = true;
                btnOk.Margin = new Thickness(0, 0, 10, 0);
                btnOk.Click += (_, __) => { DialogResult = true; Close(); };

                var btnCancel = CreateMenuButton("取消", text);
                btnCancel.IsCancel = true;
                btnCancel.Click += (_, __) => { DialogResult = false; Close(); };

                btnPanel.Children.Add(btnOk);
                btnPanel.Children.Add(btnCancel);

                Grid.SetRow(btnPanel, 2);
                root.Children.Add(btnPanel);

                outer.Child = root;

                PreviewMouseWheel += (_, e) =>
                {
                    int delta = e.Delta > 0 ? -1 : +1;
                    double nv = _slider.Value + delta;
                    if (nv < _slider.Minimum) nv = _slider.Minimum;
                    if (nv > _slider.Maximum) nv = _slider.Maximum;
                    _slider.Value = nv;
                    e.Handled = true;
                };

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
                    Padding = new Thickness(10, 6, 10, 6),
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
                    if (Application.Current?.TryFindResource(key) is Brush b) return b;
                }
                catch { }
                return new SolidColorBrush(fallback);
            }
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