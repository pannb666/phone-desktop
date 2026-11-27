using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using PhoneDesktop.Core;
using PhoneDesktop.Interop;
using PhoneDesktop.Models;
using PhoneDesktop.Overlay.Dock;
using PhoneDesktop.Services;

namespace PhoneDesktop.Drawer;

/// <summary>
/// 应用抽屉：独立的全屏顶层窗口，仅在显示期间 Topmost，关闭后立即取消置顶并隐藏。
/// 不透明背景（不启用 AllowsTransparency），避免全屏透明窗口的内存代价。
/// </summary>
public partial class DrawerWindow : Window
{
    private const int WsExToolWindow = 0x00000080;

    private static readonly TimeSpan DoubleClickWindow = TimeSpan.FromMilliseconds(500);

    private readonly Action<CatalogItem> _launch;
    private readonly DockService _dockService;
    private readonly Action<string> _toast;
    private readonly DrawerViewModel _viewModel;
    private readonly DispatcherTimer _probeTimer;

    private DrawerItemViewModel? _lastClicked;
    private DateTime _lastClickUtc = DateTime.MinValue;
    private bool _isOpen;

    public DrawerWindow(AppCatalog catalog, IconCache icons, DockService dockService, Action<CatalogItem> launch, Action<string> toast)
    {
        InitializeComponent();

        _launch = launch;
        _dockService = dockService;
        _toast = toast;
        _viewModel = new DrawerViewModel(catalog, icons);
        DataContext = _viewModel;

        _viewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(DrawerViewModel.IsEmpty) or nameof(DrawerViewModel.IsLoading))
            {
                UpdateStatus();
            }
        };

        Left = 0;
        Top = 0;
        Width = SystemParameters.PrimaryScreenWidth;
        Height = SystemParameters.PrimaryScreenHeight;

        // 目录是懒加载的：加载完成后列表会整体替换，需要刷新状态并重写探针
        _viewModel.Items.CollectionChanged += (_, _) =>
        {
            UpdateStatus();
            ScheduleProbe();
        };

        PreviewKeyDown += OnPreviewKeyDown;

        _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _probeTimer.Tick += (_, _) =>
        {
            _probeTimer.Stop();
            WriteProbe();
        };

        Loaded += (_, _) =>
        {
            UpdateStatus();
            ScheduleProbe();
        };
        SizeChanged += (_, _) => ScheduleProbe();
        SourceInitialized += (_, _) => ApplyToolWindowStyle();
    }

    /// <summary>显示抽屉（置顶 + 抢焦点 + 清空搜索）。</summary>
    public void OpenDrawer()
    {
        if (_isOpen)
        {
            Activate();
            return;
        }

        var watch = System.Diagnostics.Stopwatch.StartNew();
        AppLog.Info("OpenDrawer 开始");

        _isOpen = true;
        _lastClicked = null;

        SearchBox.Text = string.Empty;
        _viewModel.SetSearch(string.Empty);
        UpdateStatus();

        // 测试/排障用：首次打开时预置筛选词（例如空筛选用来测量窗口本体开销）
        var presetFilter = Environment.GetEnvironmentVariable("PHONEDESKTOP_DRAWER_FILTER");
        if (!string.IsNullOrWhiteSpace(presetFilter))
        {
            SearchBox.Text = presetFilter;
            _viewModel.SetSearch(presetFilter);
        }

        AppLog.Info($"OpenDrawer：清空搜索与重建列表 {watch.ElapsedMilliseconds} ms");

        Topmost = true;
        Show();
        AppLog.Info($"OpenDrawer：Show 完成 {watch.ElapsedMilliseconds} ms");

        Activate();
        AppLog.Info($"OpenDrawer：Activate 完成 {watch.ElapsedMilliseconds} ms");

        try
        {
            NativeMethods.SetForegroundWindow(new WindowInteropHelper(this).Handle);
        }
        catch
        {
            // 拿不到前台权限时仍可点击，但键盘输入可能需要先点一下窗口
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            SearchBox.Focus();
            Keyboard.Focus(SearchBox);
        }));

        _viewModel.ApplyCachedIcons();
        ScheduleProbe();
        AppLog.Info(
            $"应用抽屉已打开：{_viewModel.TotalCount} 个条目（耗时 {watch.ElapsedMilliseconds} ms，"
            + $"托管堆 {GC.GetTotalMemory(false) / (1024 * 1024)} MB）");
    }

    /// <summary>后台预热完成后由 OverlayHost 调用，一次性补齐图标。</summary>
    public void RefreshIcons() => _viewModel.ApplyCachedIcons();

    /// <summary>
    /// 关闭抽屉：取消置顶并销毁窗口。
    /// 注意不要用 Hide() 复用窗口——实测在“置顶窗口隐藏后再 Show()”时，
    /// 当前环境（Win10 + NVIDIA 叠加层 + 远程控制钩子）会阻塞约 31 秒。
    /// 每次打开重建窗口（约 180ms）更稳定，也顺带释放了可视树。
    /// </summary>
    public void CloseDrawer()
    {
        if (!_isOpen)
        {
            return;
        }

        _isOpen = false;
        Topmost = false;
        _viewModel.Release();
        DataContext = null;
        ItemsHost.ItemsSource = null;
        DevUiProbe.DeleteDrawer();
        AppLog.Info("应用抽屉已关闭");
        Close();

        // 抽屉占用的原生渲染表面不会立刻归还，主动回收一次（每次关闭只做一次，代价很小）
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    protected override void OnClosed(EventArgs e)
    {
        _probeTimer.Stop();
        DevUiProbe.DeleteDrawer();
        base.OnClosed(e);
    }

    private void ApplyToolWindowStyle()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | WsExToolWindow));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置抽屉窗口样式失败：{ex.Message}");
        }
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            CloseDrawer();
        }
    }

    private void OnBackdropMouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        CloseDrawer();
    }

    private void OnSearchTextChanged(object sender, TextChangedEventArgs e)
    {
        _viewModel.SetSearch(SearchBox.Text);
        SearchPlaceholder.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
        UpdateStatus();
        ScheduleProbe();
    }

    private void OnTileMouseDown(object sender, MouseButtonEventArgs e)
    {
        // 只吃下点击，避免冒泡到背景触发关闭
        e.Handled = true;
    }

    private void OnTileMouseUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        if (sender is not FrameworkElement { Tag: DrawerItemViewModel item })
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (ReferenceEquals(_lastClicked, item) && now - _lastClickUtc <= DoubleClickWindow)
        {
            _lastClicked = null;
            _lastClickUtc = DateTime.MinValue;
            Launch(item);
            return;
        }

        _lastClicked = item;
        _lastClickUtc = now;
    }

    /// <summary>瓦片右键：固定到 Dock（已固定则置灰）。</summary>
    private void OnTileRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DrawerItemViewModel item } element)
        {
            return;
        }

        e.Handled = true;

        AppLog.Info($"抽屉右键：{item.Name}");
        var pinned = _dockService.IsPinned(item.Path);
        var menu = new ContextMenu { PlacementTarget = element, Placement = PlacementMode.MousePoint };

        var pin = new MenuItem { Header = pinned ? "已固定到 Dock" : "固定到 Dock", IsEnabled = !pinned };
        pin.Click += (_, _) =>
        {
            var added = _dockService.Add(item.Model);
            _toast(added is not null ? $"已固定到 Dock：{item.Name}" : "已经在 Dock 里了");
        };
        menu.Items.Add(pin);

        menu.IsOpen = true;
        menu.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (menu.Items.Count > 0 && menu.Items[0] is MenuItem first)
            {
                first.Focus();
            }
        }), DispatcherPriority.Input);
    }

    private void Launch(DrawerItemViewModel item)
    {
        CloseDrawer();
        AppLog.Info($"抽屉启动：{item.Name} → {item.Path}");
        _launch(item.Model);
    }

    private void UpdateStatus()
    {
        if (_viewModel.IsLoading)
        {
            StatusText.Text = "正在加载应用…";
            StatusPanel.Visibility = Visibility.Visible;
            return;
        }

        if (_viewModel.IsEmpty)
        {
            StatusText.Text = string.IsNullOrEmpty(_viewModel.Query) ? "没有可显示的应用" : "没有匹配的结果";
            StatusPanel.Visibility = Visibility.Visible;
            return;
        }

        StatusPanel.Visibility = Visibility.Collapsed;
    }

    private void ScheduleProbe()
    {
        if (!App.IsDevMode || !_isOpen)
        {
            return;
        }

        _probeTimer.Stop();
        _probeTimer.Start();
    }

    private void WriteProbe()
    {
        if (!App.IsDevMode || !_isOpen || !IsVisible)
        {
            return;
        }

        var items = new List<Dictionary<string, object?>>();
        for (var index = 0; index < _viewModel.Items.Count; index++)
        {
            var viewModel = _viewModel.Items[index];
            var container = ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) as FrameworkElement;
            var rect = container is null ? null : DevUiProbe.RectOf(container);

            items.Add(new Dictionary<string, object?>
            {
                ["name"] = viewModel.Name,
                ["path"] = viewModel.Path,
                ["target"] = viewModel.TargetPath,
                ["x"] = rect?.X ?? 0,
                ["y"] = rect?.Y ?? 0,
                ["w"] = rect?.Width ?? 0,
                ["h"] = rect?.Height ?? 0,
            });
        }

        DevUiProbe.WriteDrawer(new Dictionary<string, object?>
        {
            ["window"] = DevUiProbe.RectOf(Root),
            ["search"] = DevUiProbe.RectOf(SearchPanel),
            ["query"] = SearchBox.Text,
            ["total"] = _viewModel.TotalCount,
            ["filtered"] = _viewModel.FilteredCount,
            ["sorted"] = _viewModel.IsSorted,
            ["items"] = items,
        });
    }
}
