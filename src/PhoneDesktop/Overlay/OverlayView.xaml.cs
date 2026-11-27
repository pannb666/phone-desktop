using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using PhoneDesktop.Core;
using PhoneDesktop.Overlay.Dock;

namespace PhoneDesktop.Overlay;

public partial class OverlayView : UserControl
{
    private bool _devMode;
    private DispatcherTimer? _toastTimer;
    private DispatcherTimer? _probeTimer;

    public OverlayView()
    {
        InitializeComponent();
        Root.MouseLeftButtonDown += OnRootMouseLeftButtonDown;
        Loaded += (_, _) =>
        {
            ApplyLayout();
            ScheduleOverlayProbe();
        };
        SizeChanged += (_, _) => ScheduleOverlayProbe();
    }

    /// <summary>点击右下角悬浮按钮，请求打开应用抽屉。</summary>
    public event EventHandler? DrawerRequested;

    /// <summary>
    /// 设置背景：有壁纸位图就铺满绘制，否则使用纯色兜底。
    /// 两者都是不透明绘制，覆盖层窗口本身不使用任何透明通道。
    /// </summary>
    public void SetBackdrop(BitmapSource? image, Color solidColor)
    {
        var brush = new SolidColorBrush(solidColor);
        brush.Freeze();
        Root.Background = brush;

        if (image is not null)
        {
            WallpaperImage.Source = image;
            WallpaperImage.Visibility = Visibility.Visible;
        }
        else
        {
            WallpaperImage.Source = null;
            WallpaperImage.Visibility = Visibility.Collapsed;
        }
    }

    public void SetDevBadge(bool visible)
    {
        _devMode = visible;
        DevBadge.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public void AttachDock(DockViewModel viewModel)
    {
        DockLayer.Children.Clear();
        DockLayer.Children.Add(new DockView { DataContext = viewModel });
    }

    public void ShowToast(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;

        _toastTimer ??= CreateToastTimer();
        _toastTimer.Stop();
        _toastTimer.Start();
    }

    private DispatcherTimer CreateToastTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(2600) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            Toast.Visibility = Visibility.Collapsed;
        };
        return timer;
    }

    /// <summary>透明桌面模式：不画自己的壁纸，让真实桌面（含动态壁纸）透出来。</summary>
    public void SetTransparentBackdrop()
    {
        WallpaperImage.Source = null;
        WallpaperImage.Visibility = Visibility.Collapsed;
        Root.Background = System.Windows.Media.Brushes.Transparent;
        DrawerButton.Visibility = Visibility.Visible;
    }

    private void OnDrawerButtonClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        AppLog.Info("悬浮按钮：请求打开应用抽屉");
        DrawerRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>悬浮按钮与轻提示随任务栏显隐调整位置。</summary>
    private void ApplyLayout()
    {
        DrawerButton.Margin = new Thickness(0, 0, 28, ShellLayout.DrawerButtonBottomMargin);
        Toast.Margin = new Thickness(0, 0, 0, ShellLayout.ToastBottomMargin);
    }

    private void ScheduleOverlayProbe()
    {
        if (!App.IsDevMode)
        {
            return;
        }

        _probeTimer ??= CreateProbeTimer();
        _probeTimer.Stop();
        _probeTimer.Start();
    }

    private DispatcherTimer CreateProbeTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            DevUiProbe.SetSections(new Dictionary<string, object?>
            {
                ["drawerButton"] = DevUiProbe.RectOf(DrawerButton),
            });
        };
        return timer;
    }

    /// <summary>
    /// 开发期输入探针：证明“挂到桌面的子窗口”仍然正常接收 WPF 鼠标事件。
    /// 阶段 2 的 Dock 点击/拖拽依赖这一点。
    /// </summary>
    private void OnRootMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_devMode)
        {
            return;
        }

        // Dock 自己处理点击时不再刷角标
        if (e.OriginalSource is DependencyObject source && IsInsideDock(source))
        {
            return;
        }

        var point = e.GetPosition(Root);
        AppLog.Info($"覆盖层收到鼠标左键：({point.X:F0},{point.Y:F0})");
        DevBadgeText.Text = $"点击已接收 ({point.X:F0},{point.Y:F0})";
    }

    private bool IsInsideDock(DependencyObject source)
    {
        for (var current = source; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (current is DockView)
            {
                return true;
            }
        }

        return false;
    }
}
