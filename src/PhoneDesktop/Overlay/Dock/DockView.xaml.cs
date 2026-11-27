using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using PhoneDesktop.Core;
using PhoneDesktop.Interop;

namespace PhoneDesktop.Overlay.Dock;

public partial class DockView : UserControl
{
    private static readonly TimeSpan RightDoubleClickWindow = TimeSpan.FromMilliseconds(500);

    private readonly DispatcherTimer _hintTimer;
    private readonly DispatcherTimer _probeTimer;
    private DateTime _lastRightClickUtc = DateTime.MinValue;

    // 拖拽排序状态
    private DockItemViewModel? _dragItem;
    private Point _dragStart;
    private bool _dragging;
    private bool _suppressClick;

    public DockView()
    {
        InitializeComponent();

        _hintTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _hintTimer.Tick += (_, _) =>
        {
            _hintTimer.Stop();
            ExitHint.Visibility = Visibility.Collapsed;
        };

        DataContextChanged += (_, _) =>
        {
            if (ViewModel is not null)
            {
                ViewModel.Running.CollectionChanged += (_, _) => ScheduleProbe();
            }
        };

        _probeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _probeTimer.Tick += (_, _) =>
        {
            // dev 模式下来回刷新：运行项的状态（激活/最小化）变化不是集合变化，靠定时刷新最稳
            WriteProbe();
        };

        if (App.IsDevMode)
        {
            _probeTimer.Start();
        }

        Loaded += OnLoaded;
        SizeChanged += (_, _) => ScheduleProbe();
    }

    private DockViewModel? ViewModel => DataContext as DockViewModel;

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 任务栏隐藏时 Dock 贴屏幕底部，否则贴在工作区底部（任务栏之上）
        Root.Margin = new Thickness(0, 0, 0, ShellLayout.DockBottomMargin);

        var taskManagerPath = Path.Combine(Environment.SystemDirectory, "taskmgr.exe");
        TaskManagerIcon.Source = ShellIcon.Load(taskManagerPath, 64);

        if (ViewModel is not null)
        {
            ViewModel.Items.CollectionChanged += (_, _) => ScheduleProbe();
        }

        ScheduleProbe();
    }

    private void OnTaskManagerClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ViewModel?.LaunchTaskManager();
    }

    private void OnItemMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DockItemViewModel item } element)
        {
            return;
        }

        e.Handled = true;
        _dragItem = item;
        _dragStart = e.GetPosition(ItemsHost);
        _dragging = false;
        _suppressClick = false;
        element.CaptureMouse();
    }

    private void OnItemMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragItem is null)
        {
            return;
        }

        var position = e.GetPosition(ItemsHost);
        if (!_dragging)
        {
            if (Math.Abs(position.X - _dragStart.X) < 4)
            {
                return;
            }

            _dragging = true;
        }

        var target = FindIndexAt(position);
        var current = ViewModel?.Items.IndexOf(_dragItem) ?? -1;
        if (target >= 0 && current >= 0 && target != current)
        {
            ViewModel?.Move(current, target);
        }
    }

    private void OnItemMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement element)
        {
            element.ReleaseMouseCapture();
        }

        e.Handled = true;

        var item = _dragItem;
        var dragged = _dragging;
        _dragItem = null;
        _dragging = false;

        if (dragged)
        {
            _suppressClick = true;
            ViewModel?.PersistOrder();
            AppLog.Info("Dock 顺序已更新");
            ScheduleProbe();
            return;
        }

        if (!_suppressClick && item is not null)
        {
            ViewModel?.Launch(item);
        }

        _suppressClick = false;
    }

    private int FindIndexAt(Point position)
    {
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return -1;
        }

        for (var index = 0; index < viewModel.Items.Count; index++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }

            var origin = container.TranslatePoint(new Point(0, 0), ItemsHost);
            if (position.X >= origin.X && position.X <= origin.X + container.ActualWidth)
            {
                return index;
            }
        }

        return -1;
    }

    private void OnItemRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: DockItemViewModel item } element)
        {
            return;
        }

        e.Handled = true;

        AppLog.Info($"Dock 右键：{item.Name}");
        var menu = new ContextMenu { PlacementTarget = element, Placement = PlacementMode.MousePoint };

        var launch = new MenuItem { Header = "打开" };
        launch.Click += (_, _) => ViewModel?.Launch(item);
        menu.Items.Add(launch);

        var unpin = new MenuItem { Header = "取消固定" };
        unpin.Click += (_, _) => ViewModel?.Unpin(item);
        menu.Items.Add(unpin);

        OpenMenu(menu);
    }

    private void OnDockRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var menu = new ContextMenu { PlacementTarget = DockPill, Placement = PlacementMode.MousePoint };

        var drawer = new MenuItem { Header = "打开应用抽屉" };
        drawer.Click += (_, _) => ViewModel?.OpenDrawer();
        menu.Items.Add(drawer);

        var settings = new MenuItem { Header = "设置…" };
        settings.Click += (_, _) => ViewModel?.OpenSettings();
        menu.Items.Add(settings);

        AppLog.Info("Dock 空白处右键：显示菜单");
        OpenMenu(menu);
    }

    /// <summary>程序化打开的 ContextMenu 不会自动获得键盘焦点，这里把焦点放到第一项，键盘也能操作。</summary>
    private static void OpenMenu(ContextMenu menu)
    {
        menu.IsOpen = true;
        menu.Dispatcher.BeginInvoke(new Action(() =>
        {
            if (menu.Items.Count > 0 && menu.Items[0] is MenuItem first)
            {
                first.Focus();
            }
        }), DispatcherPriority.Input);
    }

    private void OnSettingsClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ViewModel?.OpenSettings();
    }

    private void OnTrayClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string kind })
        {
            return;
        }

        e.Handled = true;
        switch (kind)
        {
            case "wifi":
                ViewModel?.OpenNetwork();
                break;
            case "bluetooth":
                ViewModel?.OpenBluetoothSettings();
                break;
            case "volume":
                ViewModel?.ToggleMute();
                break;
            case "ime":
                ViewModel?.CycleInputLanguage();
                break;
            case "clock":
                ViewModel?.RefreshClock();
                break;
        }
    }

    private void OnRunningClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { Tag: RunningAppViewModel item })
        {
            e.Handled = true;
            ViewModel?.ToggleRunning(item);
        }
    }

    private void OnRunningRightClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: RunningAppViewModel item } element)
        {
            return;
        }

        e.Handled = true;

        var menu = new ContextMenu { PlacementTarget = element, Placement = PlacementMode.MousePoint };

        var activate = new MenuItem { Header = item.IsMinimized ? "恢复窗口" : "切换到该窗口" };
        activate.Click += (_, _) => ViewModel?.ToggleRunning(item);
        menu.Items.Add(activate);

        var minimize = new MenuItem { Header = "最小化", IsEnabled = !item.IsMinimized };
        minimize.Click += (_, _) => ViewModel?.MinimizeRunning(item);
        menu.Items.Add(minimize);

        var close = new MenuItem { Header = "关闭窗口" };
        close.Click += (_, _) => ViewModel?.CloseRunning(item);
        menu.Items.Add(close);

        AppLog.Info($"运行项右键：{item.Title}");
        OpenMenu(menu);
    }

    private void OnExitLeftClick(object sender, MouseButtonEventArgs e)
    {
        // 左键单击直接退出（原 PRD 写的是“左键无动作、右键双击退出”，
        // 实测这样用户会以为按钮坏了，因此改为左键也可退出，右键双击保留）。
        e.Handled = true;
        AppLog.Info("退出按钮左键单击：退出");
        ViewModel?.RequestExit();
    }

    private void OnExitRightClick(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;

        var now = DateTime.UtcNow;
        if (now - _lastRightClickUtc <= RightDoubleClickWindow)
        {
            _lastRightClickUtc = DateTime.MinValue;
            _hintTimer.Stop();
            ExitHint.Visibility = Visibility.Collapsed;
            ViewModel?.RequestExit();
            return;
        }

        _lastRightClickUtc = now;
        ShowExitHint("再右键一次退出");
        AppLog.Info("退出按钮右键一次：显示“再右键一次退出”提示");
    }

    private void ShowExitHint(string text)
    {
        ExitHintText.Text = text;
        ExitHint.Visibility = Visibility.Visible;
        _hintTimer.Stop();
        _hintTimer.Start();
    }

    private void ScheduleProbe()
    {
        if (!App.IsDevMode)
        {
            return;
        }

        _probeTimer.Stop();
        _probeTimer.Start();
    }

    private void WriteProbe()
    {
        if (!App.IsDevMode || !IsLoaded)
        {
            return;
        }

        DevUiProbe.SetSections(new Dictionary<string, object?>
        {
            ["dock"] = DevUiProbe.RectOf(DockPill),
            ["taskManager"] = DevUiProbe.RectOf(TaskManagerButton),
            ["settings"] = DevUiProbe.RectOf(SettingsButton),
            ["exit"] = DevUiProbe.RectOf(ExitButton),
            ["items"] = BuildItemRects(),
            ["running"] = BuildRunningRects(),
            ["tray"] = new Dictionary<string, object?>
            {
                ["wifi"] = DevUiProbe.RectOf(WifiButton),
                ["bluetooth"] = DevUiProbe.RectOf(BluetoothButton),
                ["volume"] = DevUiProbe.RectOf(VolumeButton),
                ["ime"] = DevUiProbe.RectOf(ImeButton),
                ["clock"] = DevUiProbe.RectOf(ClockButton),
            },
        });
    }

    private List<ProbeRunning> BuildRunningRects()
    {
        var list = new List<ProbeRunning>();
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return list;
        }

        for (var index = 0; index < viewModel.Running.Count; index++)
        {
            if (RunningHost.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }

            var rect = DevUiProbe.RectOf(container);
            if (rect is null)
            {
                continue;
            }

            var item = viewModel.Running[index];
            var state = item.IsActive ? "active" : item.IsMinimized ? "minimized" : "normal";
            list.Add(new ProbeRunning(item.Key, item.Title, state, rect.X, rect.Y, rect.Width, rect.Height));
        }

        return list;
    }

    private List<ProbeItem> BuildItemRects()
    {
        var list = new List<ProbeItem>();
        var viewModel = ViewModel;
        if (viewModel is null)
        {
            return list;
        }

        for (var index = 0; index < viewModel.Items.Count; index++)
        {
            if (ItemsHost.ItemContainerGenerator.ContainerFromIndex(index) is not FrameworkElement container)
            {
                continue;
            }

            var rect = DevUiProbe.RectOf(container);
            if (rect is null)
            {
                continue;
            }

            list.Add(new ProbeItem(
                viewModel.Items[index].Name,
                viewModel.Items[index].Path,
                rect.X,
                rect.Y,
                rect.Width,
                rect.Height));
        }

        return list;
    }
}
