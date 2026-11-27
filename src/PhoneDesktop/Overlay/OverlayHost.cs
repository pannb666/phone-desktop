using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using PhoneDesktop.Core;
using PhoneDesktop.Drawer;
using PhoneDesktop.Interop;
using PhoneDesktop.Models;
using PhoneDesktop.Overlay.Dock;
using PhoneDesktop.Services;

namespace PhoneDesktop.Overlay;

public sealed record OverlayOptions(bool ShowDebugBadge, bool TransparentDesktop = false);

/// <summary>
/// 桌面覆盖层的宿主。
///
/// 关键点：直接创建 “父窗口 = Progman 的 WS_CHILD 窗口”，而不是先建顶层窗口再 SetParent。
///   - 不经过顶层窗口阶段，没有闪烁，也不会短暂出现在任务栏 / Alt+Tab；
///   - 层级由父窗口模型保证：任何顶层窗口（含全屏游戏、任务栏）都在它之上；
///   - z 序上排在 SHELLDLL_DefView 之前，从而盖住桌面图标与原生壁纸。
/// </summary>
public sealed class OverlayHost : IDisposable
{
    private readonly DesktopHost _desktop;
    private readonly WallpaperService _wallpaper;
    private readonly SettingsService _settingsService;
    private readonly AppSettings _settings;
    private readonly OverlayOptions _options;
    private readonly DispatcherTimer _guard;
    private readonly IconCache _icons;
    private readonly AppCatalog _catalog;
    private readonly DockService _dockService;
    private readonly RunningAppsService _runningApps = new();
    private readonly DockViewModel _dockViewModel;

    private HwndSource? _source;
    private Window? _transparentWindow;
    private OverlayView? _view;
    private DrawerWindow? _drawer;
    private int _width;
    private int _height;
    private bool _disposed;

    public OverlayHost(DesktopHost desktop, WallpaperService wallpaper, SettingsService settingsService, OverlayOptions options)
    {
        _desktop = desktop;
        _wallpaper = wallpaper;
        _settingsService = settingsService;
        _settings = settingsService.Settings;
        _options = options;

        _icons = new IconCache();
        _catalog = new AppCatalog();
        _catalog.Ready += (_, _) => StartIconWarmup();

        _dockService = new DockService(settingsService, _icons);
        _dockService.Load();
        _dockViewModel = new DockViewModel(_dockService, _runningApps, _icons, RequestExit, ShowToast, RequestSettings, OpenDrawer);

        _guard = new DispatcherTimer(DispatcherPriority.Background)
        {
            // 桌面切换（Win+D / 显示桌面）时 Explorer 会把桌面子窗口重挂到 WorkerW，
            // 并可能把 DefView 放到覆盖层之上；1 秒一次的纯 Win32 检查代价可忽略，
            // 但能把“图标短暂盖住覆盖层”的窗口压到 1 秒内。
            Interval = TimeSpan.FromSeconds(1),
        };
        _guard.Tick += OnGuardTick;
    }

    /// <summary>用户在 Dock 上右键双击退出按钮时触发。</summary>
    public event EventHandler? ExitRequested;

    /// <summary>用户请求打开设置页（Dock 齿轮按钮 / 右键菜单）。</summary>
    public event EventHandler? SettingsRequested;

    /// <summary>供全局快捷键调用：打开应用抽屉。</summary>
    public void ShowDrawer() => OpenDrawer();

    /// <summary>兜底色变更后重新绘制壁纸层。</summary>
    public void RefreshBackdrop() => ApplyWallpaper();

    public IntPtr Handle => _source?.Handle ?? IntPtr.Zero;

    public bool IsAttached => Handle != IntPtr.Zero && NativeMethods.IsWindow(Handle);

    public void Attach()
    {
        if (!_desktop.TryLocate())
        {
            AppLog.Warn("未定位到桌面窗口（Progman / WorkerW），覆盖层未创建");
            return;
        }

        if (_options.TransparentDesktop)
        {
            CreateTransparentWindow();
            return;
        }

        CreateSource();

        if (IsAttached)
        {
            _guard.Start();
        }
    }

    /// <summary>
    /// 透明桌面模式：不接管整块桌面，而是隐藏桌面图标、用一个“不激活的透明顶层窗口”承载 Dock 与悬浮按钮。
    /// 这样 Wallpaper Engine 等动态壁纸可以完整显示，普通窗口依旧会盖住 Dock。
    /// </summary>
    private void CreateTransparentWindow()
    {
        _desktop.SetDesktopIconsVisible(false);

        _width = _desktop.Width;
        _height = _desktop.Height;

        _view = new OverlayView();
        ApplyWallpaper();
        _view.SetTransparentBackdrop();
        _view.SetDevBadge(_options.ShowDebugBadge);
        _view.AttachDock(_dockViewModel);
        _view.DrawerRequested += (_, _) => OpenDrawer();

        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = System.Windows.Media.Brushes.Transparent,
            ShowInTaskbar = false,
            Topmost = false,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            Width = _width,
            Height = _height,
            Content = _view,
            Title = "PhoneDesktopDockLayer",
        };

        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(
                handle,
                NativeMethods.GWL_EXSTYLE,
                new IntPtr(exStyle | 0x00000080 | 0x08000000));   // WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE
        };

        window.Show();
        _transparentWindow = window;
        _guard.Start();
        AppLog.Info($"透明桌面模式已启用：{_width}x{_height}，桌面图标已隐藏，Dock 浮于桌面之上");
    }

    private void CreateSource()
    {
        DestroySource();

        _width = _desktop.Width;
        _height = _desktop.Height;

        var parameters = new HwndSourceParameters("PhoneDesktopOverlay")
        {
            ParentWindow = _desktop.IconLayerHost != IntPtr.Zero ? _desktop.IconLayerHost : _desktop.Progman,
            // 先隐藏创建：置顶完成后再由 EnsureChildOnTop 前的 SetWindowPos(SWP_SHOWWINDOW) 显示，
            // 避免“已可见但仍在图标层之下”的一瞬间。
            WindowStyle = NativeMethods.WS_CHILD
                          | NativeMethods.WS_CLIPCHILDREN
                          | NativeMethods.WS_CLIPSIBLINGS,
            ExtendedWindowStyle = 0,
            PositionX = 0,
            PositionY = 0,
            Width = Math.Max(1, _width),
            Height = Math.Max(1, _height),
        };

        try
        {
            _source = new HwndSource(parameters);
        }
        catch (Exception ex)
        {
            AppLog.Error("覆盖层窗口创建失败", ex);
            _source = null;
            return;
        }

        _view = new OverlayView();
        ApplyWallpaper();
        _view.SetDevBadge(_options.ShowDebugBadge);
        _view.AttachDock(_dockViewModel);
        _view.DrawerRequested += (_, _) => OpenDrawer();
        _source.RootVisual = _view;

        var hwnd = _source.Handle;
        AppLog.Info(
            $"覆盖层已挂载 hwnd=0x{hwnd.ToInt64():X8} 宿主={NativeMethods.ClassNameOf(_desktop.IconLayerHost)}"
            + $" size={_width}x{_height}");

        if (!_desktop.EnsureChildAttached(hwnd))
        {
            AppLog.Warn("覆盖层未能排到桌面图标层之上，等待守护重试");
        }
    }

    private void ApplyWallpaper()
    {
        if (_view is null)
        {
            return;
        }

        var result = _wallpaper.Load(_desktop.Width, _desktop.Height, _settings.ResolveFallbackColor());
        _view.SetBackdrop(result.Image, result.SolidColor);
        AppLog.Info($"覆盖层背景已应用：{result.Description}");
    }

    private void OnGuardTick(object? sender, EventArgs e)
    {
        if (_options.TransparentDesktop)
        {
            EnsureTransparentLayerVisible();
            return;
        }

        OnChildGuardTick(sender, e);
    }

    /// <summary>
    /// 透明模式下的守护：Win+D / “显示桌面”会把我们的顶层 Dock 层一并最小化，
    /// 这里把它无激活地重新显示出来（不改变 z 序，普通窗口仍然盖得住）。
    /// </summary>
    private void EnsureTransparentLayerVisible()
    {
        if (_transparentWindow is null)
        {
            return;
        }

        var handle = new WindowInteropHelper(_transparentWindow).Handle;
        if (handle == IntPtr.Zero)
        {
            return;
        }

        var hidden = !NativeMethods.IsWindowVisible(handle) || NativeMethods.IsIconic(handle);
        if (hidden)
        {
            NativeMethods.ShowWindow(handle, 4);   // SW_SHOWNOACTIVATE
            AppLog.Info("透明模式：Dock 层被系统隐藏/最小化，已无激活恢复");
        }

        // Win+D 会把桌面提到最前，我们的窗口虽然还在，却压到了桌面之下；
        // 只有当 Dock 位置露出来的是桌面本身时才重新提升，普通窗口盖住时保持不动。
        var probe = new NativeMethods.POINT
        {
            X = Math.Max(1, _width / 2),
            Y = Math.Max(1, _height - 60),
        };
        var top = NativeMethods.WindowFromPoint(probe);
        var cls = top == IntPtr.Zero ? string.Empty : NativeMethods.ClassNameOf(top);
        var desktopOnTop = cls is "Progman" or "WorkerW" or "SHELLDLL_DefView" or "SysListView32";

        if (!desktopOnTop)
        {
            return;
        }

        NativeMethods.SetWindowPos(
            handle,
            NativeMethods.HWND_TOP,
            0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        AppLog.Info("透明模式：Dock 层已重新浮到桌面之上");
    }

    private void OnChildGuardTick(object? sender, EventArgs e)
    {
        if (_disposed)
        {
            return;
        }

        if (!_desktop.IsValid)
        {
            AppLog.Warn("桌面窗口失效（Explorer 可能重启），重新定位并挂载");
            if (_desktop.TryLocate())
            {
                CreateSource();
            }

            return;
        }

        if (!IsAttached)
        {
            AppLog.Warn("覆盖层窗口已失效，重新创建");
            CreateSource();
            return;
        }

        var previousWidth = _width;
        var previousHeight = _height;
        if (!_desktop.TryLocate())
        {
            return;
        }

        if (_desktop.Width != previousWidth || _desktop.Height != previousHeight)
        {
            AppLog.Info($"桌面尺寸变化：{previousWidth}x{previousHeight} -> {_desktop.Width}x{_desktop.Height}");
            ResizeSource();
            return;
        }

        _desktop.EnsureChildAttached(Handle);
    }

    private void ResizeSource()
    {
        _width = _desktop.Width;
        _height = _desktop.Height;

        NativeMethods.SetWindowPos(
            Handle,
            IntPtr.Zero,
            0,
            0,
            _width,
            _height,
            NativeMethods.SWP_NOZORDER | NativeMethods.SWP_NOACTIVATE);

        ApplyWallpaper();
        _desktop.EnsureChildAttached(Handle);
    }

    private void RequestExit() => ExitRequested?.Invoke(this, EventArgs.Empty);

    /// <summary>在覆盖层顶部显示一条轻提示。</summary>
    public void ShowToast(string message) => _view?.ShowToast(message);

    private void RequestSettings() => SettingsRequested?.Invoke(this, EventArgs.Empty);

    private void OpenDrawer()
    {
        // 目录扫描与图标预热延迟到首次打开抽屉时进行：
        // 不开抽屉时不加载 shell 组件与 178 个图标，空闲内存保持在低位。
        _catalog.StartLoad();

        // 每次打开重建窗口：避免复用隐藏的置顶窗口（实测二次 Show 会阻塞数十秒）
        if (_drawer is not null)
        {
            try
            {
                _drawer.Close();
            }
            catch
            {
                // 忽略
            }

            _drawer = null;
        }

        _drawer = new DrawerWindow(_catalog, _icons, _dockService, LaunchFromDrawer, ShowToast);
        _drawer.OpenDrawer();
    }

    private void StartIconWarmup()
    {
        var items = _catalog.Items;
        AppLog.Info($"开始预热图标：{items.Count} 项");
        IconWarmup.Start(items, _icons, DrawerViewModel.IconSize, () =>
        {
            AppLog.Info($"图标预热完成（托管堆 {GC.GetTotalMemory(false) / (1024 * 1024)} MB）");
            _drawer?.RefreshIcons();
        });
    }

    private void LaunchFromDrawer(CatalogItem item)
    {
        if (ShellLauncher.TryLaunch(item.Path, out var error))
        {
            AppLog.Info($"抽屉启动成功：{item.DisplayName}");
            return;
        }

        AppLog.Warn($"抽屉启动失败：{item.DisplayName}（{error}）");
        _view?.ShowToast($"启动失败：{item.DisplayName}");
    }

    private void DestroySource()
    {
        var source = _source;
        _source = null;
        _view = null;

        if (source is null)
        {
            return;
        }

        try
        {
            source.RootVisual = null;
        }
        catch
        {
            // 父窗口已销毁时可能抛异常，忽略
        }

        try
        {
            source.Dispose();
        }
        catch
        {
            // 忽略
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _guard.Stop();
        _guard.Tick -= OnGuardTick;

        try
        {
            _transparentWindow?.Close();
            _transparentWindow = null;

            if (_options.TransparentDesktop)
            {
                _desktop.SetDesktopIconsVisible(true);
            }

            _drawer?.Close();
        }
        catch
        {
            // 忽略
        }

        _drawer = null;
        DestroySource();
    }
}
