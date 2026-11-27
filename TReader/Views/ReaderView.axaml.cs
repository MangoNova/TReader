using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using AvaloniaEdit;
using TReader.ViewModels;

namespace TReader.Views;

public partial class ReaderView : UserControl
{
    private TextEditor? _textEditor;

    public ReaderView()
    {
        InitializeComponent();

        _textEditor = this.FindControl<TextEditor>("TextEditor");

        DataContextChanged += OnDataContextChanged;

        // 使用 Tunnel 策略拦截鼠标按下事件，优先处理窗口拖动，阻止 TextEditor 的选择行为
        // 这个 Handler 会在 Tunneling 阶段触发（即从父级到子级），先于 TextEditor 的处理
        this.AddHandler(PointerPressedEvent, OnPreviewPointerPressed, RoutingStrategies.Tunnel);

        // 拦截方向键，避免 TextEditor 移动光标导致进度跳回开头
        this.AddHandler(Avalonia.Input.InputElement.KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);

        // 监听滚动事件以保存阅读进度
        if (_textEditor?.TextArea?.TextView != null)
        {
            _textEditor.TextArea.TextView.ScrollOffsetChanged += OnScrollOffsetChanged;
        }
    }

    // 标志位：正在初始化滚动位置，暂不保存进度
    private bool _isInitializingScroll = false;
    private DateTime _lastScrollSaveTime = DateTime.MinValue;

    private void OnScrollOffsetChanged(object? sender, EventArgs e)
    {
        // 初始化期间不保存进度
        if (_isInitializingScroll) return;

        // 获取当前可见区域顶部的文档行，窗口大小变化时比中间行更稳定
        if (_textEditor?.TextArea?.TextView == null || DataContext is not ReaderViewModel vm) return;

        // 窗口缩放期间宽度变化会导致换行重排，同一 ScrollOffset 对应的行号会漂移，
        // 此时暂停进度更新，避免阅读位置被错误改写；缩放结束后由 RequestScrollToCurrentLine 重新对齐。
        if (vm.IsWindowResizing) return;

        var textView = _textEditor.TextArea.TextView;
        var topLine = textView.GetDocumentLineByVisualTop(textView.ScrollOffset.Y);
        if (topLine == null) return;

        // AvaloniaEdit 的 LineNumber 从 1 开始，统一转换为 0-based
        // 先立即更新当前行，避免按书签时读取到旧位置
        vm.UpdateCurrentLine(topLine.LineNumber - 1, saveProgress: false);

        // 写入磁盘做防抖，至少间隔 500ms 才保存一次
        if ((DateTime.Now - _lastScrollSaveTime).TotalMilliseconds < 500) return;
        _lastScrollSaveTime = DateTime.Now;
        vm.SaveProgress();
    }

    private void OnPreviewKeyDown(object? sender, Avalonia.Input.KeyEventArgs e)
    {
        if (DataContext is not ReaderViewModel vm) return;

        switch (e.Key)
        {
            case Avalonia.Input.Key.Up:
            case Avalonia.Input.Key.Left:
            case Avalonia.Input.Key.PageUp:
                ScrollPage(false);
                e.Handled = true;
                break;

            case Avalonia.Input.Key.Down:
            case Avalonia.Input.Key.Right:
            case Avalonia.Input.Key.PageDown:
                ScrollPage(true);
                e.Handled = true;
                break;

            case Avalonia.Input.Key.B:
                vm.ToggleBookmark();
                e.Handled = true;
                break;

            case Avalonia.Input.Key.M:
                vm.OpenBookmarks();
                e.Handled = true;
                break;
        }
    }

    private void ScrollPage(bool forward)
    {
        if (_textEditor?.TextArea?.TextView == null) return;

        _textEditor.ApplyTemplate();

        var textView = _textEditor.TextArea.TextView;
        var scrollable = (Avalonia.Controls.Primitives.IScrollable)_textEditor.TextArea;

        var viewportHeight = scrollable.Viewport.Height;
        if (viewportHeight <= 0) viewportHeight = _textEditor.ViewportHeight;
        if (viewportHeight <= 0) viewportHeight = _textEditor.Bounds.Height;
        if (viewportHeight <= 0) return;

        var lineCount = _textEditor.LineCount;
        if (lineCount <= 0) return;

        // 当前视口顶部所在的文档行（基于行高树，自动换行也适用）
        var topDocLine = textView.GetDocumentLineByVisualTop(scrollable.Offset.Y);
        if (topDocLine == null) return;
        int topLine = topDocLine.LineNumber;

        // 该行在文档中的精确顶部 Y（1-based 行号）
        double topLineY = textView.GetVisualTopByDocumentLine(topLine);

        // 计算满一屏所覆盖的文档行数：找到顶部 Y 落在 [topLineY, topLineY + viewportHeight) 内的最后一行
        // 下一页从"当前页最后完整可见行"的下一行开始，保证每页顶部都是完整行的边界，
        // 不会出现一行被上下两页各显示一半的情况。
        int nextLine = topLine;
        if (forward)
        {
            for (int line = topLine + 1; line <= lineCount; line++)
            {
                double lineY = textView.GetVisualTopByDocumentLine(line);
                if (lineY - topLineY >= viewportHeight - 0.5) break;
                nextLine = line;
            }
            // 一行高度超过整屏时至少前进一行，避免卡住
            if (nextLine == topLine && topLine < lineCount) nextLine = topLine + 1;
        }
        else
        {
            // 向后翻页：找到最后一个顶部仍 >= topLineY - viewportHeight 的行作为新页顶部
            for (int line = topLine - 1; line >= 1; line--)
            {
                double lineY = textView.GetVisualTopByDocumentLine(line);
                if (topLineY - lineY >= viewportHeight - 0.5) break;
                nextLine = line;
            }
            // 一行高度超过整屏时至少后退一行，避免卡住
            if (nextLine == topLine && topLine > 1) nextLine = topLine - 1;
        }

        double targetY = textView.GetVisualTopByDocumentLine(nextLine);
        var maxOffset = Math.Max(0, scrollable.Extent.Height - viewportHeight);
        targetY = Math.Clamp(targetY, 0, maxOffset);

        if (Math.Abs(targetY - scrollable.Offset.Y) < 0.5) return;

        scrollable.Offset = new Vector(scrollable.Offset.X, targetY);
    }

    private void OnPreviewPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        // 只有左键才触发拖动
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
        {
            // 检查是否在 Resize 控件上，如果是，则不在此处拦截，交给 OnResize（Bubble阶段）或其它逻辑
            if (e.Source is Control control && control.Tag != null && !string.IsNullOrEmpty(control.Tag.ToString()))
            {
                // 是 Resize 手柄，允许事件继续传递，让 bubble 阶段的 OnResize 处理
                return;
            }

            // 是内容区域（Border 或 TextEditor），触发窗口拖动
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null)
            {
                window.BeginMoveDrag(e);
                e.Handled = true; // 标记已处理，不再向下传递给 TextEditor
            }
        }
    }

    // -----------------------------------------------------------
    // 手动窗口 Resize 逻辑
    // -----------------------------------------------------------

    private bool _isResizing;
    private WindowEdge _resizeEdge;
    private Avalonia.Point _startMousePosition;
    private Avalonia.PixelRect _startWindowRect;

    private void OnResize(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed && sender is Control control)
        {
            var edgeStr = control.Tag?.ToString();
            if (!string.IsNullOrEmpty(edgeStr) && Enum.TryParse<WindowEdge>(edgeStr, true, out var edge))
            {
                var window = TopLevel.GetTopLevel(this) as Window;
                if (window != null)
                {
                    _isResizing = true;
                    _resizeEdge = edge;
                    _startMousePosition = e.GetPosition(this);

                    // 获取当前窗口在屏幕上的位置和大小
                    var pos = window.Position;
                    // 注意：Width/Height可能是double
                    _startWindowRect = new PixelRect(pos.X, pos.Y, (int)window.Width, (int)window.Height);

                    e.Pointer.Capture(control);
                    e.Handled = true;
                }
            }
        }
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);

        if (_isResizing)
        {
            var window = TopLevel.GetTopLevel(this) as Window;
            if (window != null)
            {
                var currentMousePos = e.GetPosition(this);
                // 这里的差值是在控件坐标系下的，因为控件随窗口移动，这可能会导致抖动。
                // 更好的方式是使用 Screen 坐标。
                // 但 PointerEventArgs 没有直接提供 Screen 坐标。

                var startScreen = window.PointToScreen(_startMousePosition);
                var currentScreen = window.PointToScreen(currentMousePos);

                // PixelPoint 之间的差值是 int，转换为 double
                var deltaX = (double)(currentScreen.X - startScreen.X);
                var deltaY = (double)(currentScreen.Y - startScreen.Y);

                double newX = _startWindowRect.X;
                double newY = _startWindowRect.Y;
                double newWidth = _startWindowRect.Width;
                double newHeight = _startWindowRect.Height;

                switch (_resizeEdge)
                {
                    case WindowEdge.East:
                        newWidth += deltaX;
                        break;
                    case WindowEdge.South:
                        newHeight += deltaY;
                        break;
                    case WindowEdge.West:
                        newWidth -= deltaX;
                        newX += deltaX;
                        break;
                    case WindowEdge.North:
                        newHeight -= deltaY;
                        newY += deltaY;
                        break;
                    case WindowEdge.NorthEast:
                        newHeight -= deltaY;
                        newY += deltaY;
                        newWidth += deltaX;
                        break;
                    case WindowEdge.SouthEast:
                        newHeight += deltaY;
                        newWidth += deltaX;
                        break;
                    case WindowEdge.SouthWest:
                        newHeight += deltaY;
                        newWidth -= deltaX;
                        newX += deltaX;
                        break;
                    case WindowEdge.NorthWest:
                        newHeight -= deltaY;
                        newY += deltaY;
                        newWidth -= deltaX;
                        newX += deltaX;
                        break;
                }

                if (newWidth > 50 && newHeight > 50)
                {
                    window.Width = newWidth;
                    window.Height = newHeight;
                    window.Position = new Avalonia.PixelPoint((int)newX, (int)newY);
                }
            }
        }
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (_isResizing)
        {
            _isResizing = false;
            e.Pointer.Capture(null);
        }
    }

    // -----------------------------------------------------------
    // ViewModel 绑定逻辑
    // -----------------------------------------------------------

    private void OnDataContextChanged(object? sender, EventArgs e)
    {
        if (DataContext is ReaderViewModel vm)
        {
            vm.ScrollToLineRequested += OnScrollToLineRequested;
            vm.PropertyChanged += OnViewModelPropertyChanged;

            // 初始加载时设置文本（如果已有内容）和外观设置
            UpdateTextEditor(vm.DisplayText);
            UpdateEditorSettings(vm);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (sender is ReaderViewModel vm)
        {
            if (e.PropertyName == nameof(ReaderViewModel.DisplayText))
            {
                UpdateTextEditor(vm.DisplayText);
            }
            else if (e.PropertyName == nameof(ReaderViewModel.ForegroundBrush))
            {
                if (_textEditor != null)
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        _textEditor.Foreground = vm.ForegroundBrush;
                    });
                }
            }
            else if (e.PropertyName == nameof(ReaderViewModel.LineHeight) ||
                     e.PropertyName == nameof(ReaderViewModel.FontSize) ||
                     e.PropertyName == nameof(ReaderViewModel.ParagraphSpacing) ||
                     e.PropertyName == nameof(ReaderViewModel.FontFamily))
            {
                UpdateEditorSettings(vm);
            }
        }
    }

    private void UpdateEditorSettings(ReaderViewModel vm)
    {
        if (_textEditor == null) return;

        Dispatcher.UIThread.Post(() =>
        {
            // 使用 master 分支的 LineHeightFactor 功能设置行距
            // 确保值为正数（LineHeightFactor 要求 > 0）
            var lineHeight = vm.LineHeight;
            if (lineHeight <= 0) lineHeight = 1.5; // 默认值
            _textEditor.Options.LineHeightFactor = lineHeight;

            // 应用字体
            if (!string.IsNullOrEmpty(vm.FontFamily))
            {
                _textEditor.FontFamily = new Avalonia.Media.FontFamily(vm.FontFamily);
            }
            else
            {
                _textEditor.FontFamily = Avalonia.Media.FontFamily.Default;
            }

            // 刷新视觉
            _textEditor.InvalidateVisual();
        });
    }

    private void UpdateTextEditor(string? text)
    {
        if (_textEditor == null || string.IsNullOrEmpty(text)) return;

        Dispatcher.UIThread.Post(() =>
        {
            _textEditor.Text = text;
        });
    }

    private void OnScrollToLineRequested(int lineNumber)
    {
        // 设置标志位，防止滚动事件覆盖保存的进度
        _isInitializingScroll = true;

        // 外部传入的是原始文本 0-based 行号，需要转换为显示文本行号
        var displayLine = lineNumber;
        if (DataContext is ReaderViewModel vm)
        {
            displayLine = vm.GetDisplayLineNumber(lineNumber);
        }

        // 使用重试机制，确保文本加载完成后再滚动
        ScrollToLineWithRetry(displayLine, 10); // 最多重试10次
    }

    private void ScrollToLineWithRetry(int displayLineNumber, int retriesLeft)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_textEditor == null)
            {
                _isInitializingScroll = false;
                return;
            }

            // 如果文本已加载且行号有效，执行滚动
            // displayLineNumber 为 0-based，ScrollTo 使用 1-based 行号
            if (displayLineNumber >= 0 && displayLineNumber < _textEditor.LineCount)
            {
                _textEditor.ScrollTo(displayLineNumber + 1, -1, AvaloniaEdit.Rendering.VisualYPosition.LineTop, 0, 0);

                // 延迟重置标志位
                Dispatcher.UIThread.Post(() =>
                {
                    _isInitializingScroll = false;
                }, DispatcherPriority.Background);
            }
            else if (retriesLeft > 0)
            {
                // 文本还未加载完成，延迟 100ms 后重试
                var timer = new System.Timers.Timer(100);
                timer.Elapsed += (s, e) =>
                {
                    timer.Stop();
                    timer.Dispose();
                    ScrollToLineWithRetry(displayLineNumber, retriesLeft - 1);
                };
                timer.Start();
            }
            else
            {
                // 重试次数耗尽，重置标志位
                _isInitializingScroll = false;
            }
        }, DispatcherPriority.Background);
    }

    // 兼容可能遗留的 XAML 绑定，虽然不再使用
    private void OnPointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e) { }
}
