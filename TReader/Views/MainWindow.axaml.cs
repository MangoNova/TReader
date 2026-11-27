using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using System;
using TReader.Models;

namespace TReader.Views;

public partial class MainWindow : Window
{
    private Avalonia.Threading.DispatcherTimer _timer;
    private Avalonia.Threading.DispatcherTimer _hideDelayTimer;
    private Avalonia.Threading.DispatcherTimer _cursorHideTimer;
    private bool _isResizing;
    private int _autoHideSuspendCount;
    private WindowEdge _resizeEdge;
    private Avalonia.PixelPoint _resizeStartScreen;
    private double _startWidth;
    private double _startHeight;
    private int _startX;
    private int _startY;

    public MainWindow()
    {
        InitializeComponent();

        // 强制禁用默认缩放行为
        CanResize = false;

        var container = this.FindControl<Grid>("ContentContainer");

        // 定时器检测 Ctrl 键状态
        _timer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _timer.Tick += (s, e) =>
        {
            // 如果 Ctrl 被按下
            if (IsControlPressed())
            {
                // 强制置顶，允许用户交互（如双击）
                if (!Topmost)
                {
                    Topmost = true;
                }
            }
        };
        _timer.Start();

        // 延迟隐藏：鼠标移出后保留一小段时间，方便去拖动边缘调整大小
        _hideDelayTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1200)
        };
        _hideDelayTimer.Tick += (s, e) =>
        {
            _hideDelayTimer.Stop();

            // 缩放中、按住 Ctrl 或正在操作子窗口时不隐藏
            if (_isResizing || IsControlPressed() || _autoHideSuspendCount > 0) return;

            if (container != null) container.Opacity = 0;
            Topmost = false;
        };

        // 鼠标静止后隐藏指针
        _cursorHideTimer = new Avalonia.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(2000)
        };
        _cursorHideTimer.Tick += (s, e) =>
        {
            _cursorHideTimer.Stop();

            if (_isResizing || IsControlPressed())
            {
                RestartCursorHideTimer();
                return;
            }

            if (container != null && container.Opacity <= 0)
            {
                RestoreCursor();
                return;
            }

            TReader.Services.WindowHelper.SetCursorVisible(false);
        };

        // 监听鼠标活动，移动时恢复指针并重新计时
        this.AddHandler(
            Avalonia.Input.InputElement.PointerMovedEvent,
            OnPointerActivity,
            Avalonia.Interactivity.RoutingStrategies.Tunnel | Avalonia.Interactivity.RoutingStrategies.Bubble,
            true);

        // 鼠标进入时取消延迟隐藏
        this.PointerEntered += (s, e) =>
        {
            _hideDelayTimer.Stop();
            RestartCursorHideTimer();
        };

        // 双击显示：只有按住 Ctrl 时才响应
        this.DoubleTapped += (s, e) =>
        {
            HandlePointerActivity();

            // 检查 Ctrl 键是否被按下
            if (!IsControlPressed())
            {
                return; // Ctrl 没有按下，忽略双击
            }

            if (container != null) container.Opacity = 1;
            Topmost = true;
        };

        // 移出后延迟隐藏，避免鼠标稍微移开界面就立刻消失
        this.PointerExited += (s, e) =>
        {
            RestoreCursor();

            // 如果正在缩放、按住 Ctrl 或正在操作子窗口，不要触发隐藏
            if (_isResizing || IsControlPressed() || _autoHideSuspendCount > 0) return;

            _hideDelayTimer.Start();
        };

    }

    /// <summary>
    /// 暂停自动隐藏（打开子窗口时调用），可重入。
    /// </summary>
    public void SuspendAutoHide()
    {
        _autoHideSuspendCount++;
        _hideDelayTimer.Stop();
    }

    /// <summary>
    /// 恢复自动隐藏（子窗口关闭时调用），与 SuspendAutoHide 配对。
    /// </summary>
    public void ResumeAutoHide()
    {
        if (_autoHideSuspendCount > 0) _autoHideSuspendCount--;
    }

    /// <summary>
    /// 确保阅读窗口可见并置顶（子窗口关闭后调用）。
    /// </summary>
    public void KeepVisible()
    {
        var container = this.FindControl<Grid>("ContentContainer");
        if (container != null) container.Opacity = 1;
        Topmost = true;

        // 子窗口关闭后把键盘焦点还给阅读内容，保证翻页键继续可用
        this.FindControl<TReader.Views.ReaderView>("ReaderViewControl")?.FocusContent();
    }

    private void OnPointerActivity(object? sender, Avalonia.Input.PointerEventArgs e)
    {
        HandlePointerActivity();
    }

    private void HandlePointerActivity()
    {
        TReader.Services.WindowHelper.SetCursorVisible(true);
        RestartCursorHideTimer();
    }

    private void RestartCursorHideTimer()
    {
        _cursorHideTimer.Stop();
        if (_isResizing) return;
        _cursorHideTimer.Start();
    }

    private void RestoreCursor()
    {
        _cursorHideTimer.Stop();
        TReader.Services.WindowHelper.SetCursorVisible(true);
    }

    private static bool IsControlPressed()
    {
        return (TReader.Services.WindowHelper.GetAsyncKeyState(TReader.Services.WindowHelper.VK_CONTROL) & 0x8000) != 0;
    }

    private void OnResizePointerPressed(object? sender, Avalonia.Input.PointerPressedEventArgs e)
    {
        if (sender is not Control control) return;
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;

        var edgeText = control.Tag?.ToString();
        if (string.IsNullOrEmpty(edgeText) || !Enum.TryParse<WindowEdge>(edgeText, true, out var edge))
        {
            return;
        }

        _hideDelayTimer.Stop();
        HandlePointerActivity();
        _isResizing = true;

        // 缩放期间宽度变化会导致换行重排，暂停阅读进度更新，避免位置漂移
        if (DataContext is TReader.ViewModels.ReaderViewModel resizeVm)
        {
            resizeVm.IsWindowResizing = true;
        }

        _resizeEdge = edge;
        _resizeStartScreen = this.PointToScreen(e.GetPosition(this));
        _startWidth = Width;
        _startHeight = Height;
        _startX = Position.X;
        _startY = Position.Y;

        e.Pointer.Capture(this);
        e.Handled = true;
    }

    protected override void OnPointerMoved(Avalonia.Input.PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (!_isResizing) return;

        var currentScreen = this.PointToScreen(e.GetPosition(this));
        var deltaX = currentScreen.X - _resizeStartScreen.X;
        var deltaY = currentScreen.Y - _resizeStartScreen.Y;

        var newX = (double)_startX;
        var newY = (double)_startY;
        var newWidth = _startWidth;
        var newHeight = _startHeight;

        switch (_resizeEdge)
        {
            case WindowEdge.West:
                newWidth -= deltaX;
                newX += deltaX;
                break;
            case WindowEdge.East:
                newWidth += deltaX;
                break;
            case WindowEdge.North:
                newHeight -= deltaY;
                newY += deltaY;
                break;
            case WindowEdge.South:
                newHeight += deltaY;
                break;
            case WindowEdge.NorthWest:
                newWidth -= deltaX;
                newHeight -= deltaY;
                newX += deltaX;
                newY += deltaY;
                break;
            case WindowEdge.NorthEast:
                newWidth += deltaX;
                newHeight -= deltaY;
                newY += deltaY;
                break;
            case WindowEdge.SouthWest:
                newWidth -= deltaX;
                newHeight += deltaY;
                newX += deltaX;
                break;
            case WindowEdge.SouthEast:
                newWidth += deltaX;
                newHeight += deltaY;
                break;
        }

        // 限制最小尺寸，避免拖拽时窗口塌陷
        var clampedWidth = Math.Max(Defaults.MinWindowWidth, newWidth);
        var clampedHeight = Math.Max(Defaults.MinWindowHeight, newHeight);

        Width = clampedWidth;
        Height = clampedHeight;
        Position = new Avalonia.PixelPoint((int)newX, (int)newY);
    }

    protected override void OnPointerReleased(Avalonia.Input.PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!_isResizing) return;

        _isResizing = false;
        e.Pointer.Capture(null);
        RestartCursorHideTimer();

        // 缩放结束后恢复阅读进度更新，并把当前顶部行重新对齐到完整行的边界
        if (DataContext is TReader.ViewModels.ReaderViewModel vm)
        {
            vm.IsWindowResizing = false;
            vm.RequestScrollToCurrentLine();
        }
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);
        TReader.Services.WindowHelper.HideFromAltTab(this);
    }

    protected override void OnClosed(EventArgs e)
    {
        RestoreCursor();
        base.OnClosed(e);
    }
}