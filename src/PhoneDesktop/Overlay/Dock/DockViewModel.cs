using System.Collections.ObjectModel;
using System.Globalization;
using System.Net.NetworkInformation;
using System.Windows.Input;
using System.Windows.Threading;
using PhoneDesktop.Core;
using PhoneDesktop.Interop;
using PhoneDesktop.Models;
using PhoneDesktop.Services;

namespace PhoneDesktop.Overlay.Dock;

public sealed class DockViewModel : ObservableObject, IDisposable
{
    private readonly DockService _service;
    private readonly RunningAppsService _runningApps;
    private readonly IconCache _icons;
    private readonly Action _exit;
    private readonly Action<string> _toast;
    private readonly DispatcherTimer _runningTimer;
    private readonly DispatcherTimer _clockTimer;
    private bool _hasItems;
    private bool _hasRunning;
    private string _clockTime = string.Empty;
    private string _clockDate = string.Empty;
    private string _wifiGlyph = "\uE701";
    private double _wifiOpacity = 0.45;
    private string _wifiTooltip = "网络";
    private string _imeText = "EN";
    private string _volumeGlyph = "\uE767";

    public DockViewModel(
        DockService service,
        RunningAppsService runningApps,
        IconCache icons,
        Action exit,
        Action<string> toast,
        Action openSettings,
        Action openDrawer)
    {
        _service = service;
        _runningApps = runningApps;
        _icons = icons;
        _exit = exit;
        _toast = toast;
        OpenSettings = openSettings;
        OpenDrawer = openDrawer;

        Items = service.Items;
        Items.CollectionChanged += (_, _) => RefreshHasItems();
        RefreshHasItems();

        Running = new ObservableCollection<RunningAppViewModel>();

        // 任务栏语义：定期同步“运行中的应用”。1 秒一次 EnumWindows 的代价可以忽略。
        _runningTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
        _runningTimer.Tick += (_, _) => RefreshRunning();
        _runningTimer.Start();
        RefreshRunning();

        // 托盘：时钟每 15 秒、网络/音量/输入法每 5 秒刷新一次
        _clockTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _clockTimer.Tick += (_, _) => RefreshClock();
        _clockTimer.Start();
        RefreshClock();
        RefreshSystemStatus();
    }

    public ObservableCollection<DockItemViewModel> Items { get; }

    public ObservableCollection<RunningAppViewModel> Running { get; }

    public Action OpenSettings { get; }

    public Action OpenDrawer { get; }

    public bool HasItems
    {
        get => _hasItems;
        private set => SetProperty(ref _hasItems, value);
    }

    public bool HasRunning
    {
        get => _hasRunning;
        private set => SetProperty(ref _hasRunning, value);
    }

    /// <summary>托盘时刻（HH:mm）。</summary>
    public string ClockTime
    {
        get => _clockTime;
        private set => SetProperty(ref _clockTime, value);
    }

    /// <summary>托盘日期（M月d日 周X）。</summary>
    public string ClockDate
    {
        get => _clockDate;
        private set => SetProperty(ref _clockDate, value);
    }

    public string WifiGlyph
    {
        get => _wifiGlyph;
        private set => SetProperty(ref _wifiGlyph, value);
    }

    public double WifiOpacity
    {
        get => _wifiOpacity;
        private set => SetProperty(ref _wifiOpacity, value);
    }

    public string WifiTooltip
    {
        get => _wifiTooltip;
        private set => SetProperty(ref _wifiTooltip, value);
    }

    public string ImeText
    {
        get => _imeText;
        private set => SetProperty(ref _imeText, value);
    }

    public string VolumeGlyph
    {
        get => _volumeGlyph;
        private set => SetProperty(ref _volumeGlyph, value);
    }

    public void LaunchTaskManager()
    {
        var command = RuntimeOptions.TaskManagerCommand;
        if (ShellLauncher.TryLaunch(command, out var error))
        {
            AppLog.Info($"任务管理器按钮：已启动 {command}");
            return;
        }

        AppLog.Warn($"任务管理器启动失败：{error}");
        _toast($"无法打开任务管理器：{error}");
    }

    public void Launch(DockItemViewModel item)
    {
        if (!item.FileExists)
        {
            AppLog.Warn($"Dock 项文件缺失：{item.Path}");
            _toast($"应用不存在：{item.Name}");
            return;
        }

        if (ShellLauncher.TryLaunch(item.Path, out var error))
        {
            AppLog.Info($"Dock 启动：{item.Name} → {item.Path}");
            return;
        }

        _toast($"启动失败：{item.Name}");
    }

    public void Unpin(DockItemViewModel item)
    {
        if (_service.Remove(item))
        {
            _toast($"已取消固定：{item.Name}");
        }
    }

    public void Move(int from, int to) => _service.Move(from, to);

    public void PersistOrder() => _service.Save();

    /// <summary>运行项左键：还原/切换/最小化。</summary>
    public void ToggleRunning(RunningAppViewModel item)
    {
        _runningApps.Toggle(item.App);
        RefreshRunning();
    }

    /// <summary>运行项右键：关闭窗口。</summary>
    public void CloseRunning(RunningAppViewModel item)
    {
        if (_runningApps.Close(item.App))
        {
            _toast($"正在关闭：{item.Title}");
        }

        RefreshRunning();
    }

    /// <summary>运行项右键：最小化。</summary>
    public void MinimizeRunning(RunningAppViewModel item)
    {
        if (!item.App.IsMinimized)
        {
            _runningApps.Toggle(item.App);
        }

        RefreshRunning();
    }

    public void RefreshRunning()
    {
        IReadOnlyList<RunningApp> snapshot;
        try
        {
            snapshot = _runningApps.Snapshot();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"枚举运行中的应用失败：{ex.Message}");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in snapshot)
        {
            seen.Add(app.Key);
        }

        for (var i = Running.Count - 1; i >= 0; i--)
        {
            if (!seen.Contains(Running[i].Key))
            {
                Running.RemoveAt(i);
            }
        }

        for (var i = 0; i < snapshot.Count; i++)
        {
            var app = snapshot[i];
            var existing = FindRunning(app.Key);
            if (existing is null)
            {
                Running.Insert(Math.Min(i, Running.Count), new RunningAppViewModel(app, _icons));
                continue;
            }

            existing.Update(app);
            var currentIndex = Running.IndexOf(existing);
            if (currentIndex != i && i < Running.Count)
            {
                Running.Move(currentIndex, i);
            }
        }

        HasRunning = Running.Count > 0;
    }

    public void RequestExit()
    {
        AppLog.Info("退出按钮：退出 PhoneDesktop");
        _exit();
    }

    public void Dispose()
    {
        _runningTimer.Stop();
        _clockTimer.Stop();
    }

    public void RefreshClock()
    {
        var now = DateTime.Now;
        ClockTime = now.ToString("HH:mm", CultureInfo.InvariantCulture);
        ClockDate = now.ToString("M月d日 ddd", CultureInfo.GetCultureInfo("zh-CN"));
    }

    public void RefreshSystemStatus()
    {
        UpdateNetwork();
        UpdateIme();
    }

    /// <summary>点击网络图标：打开 Windows 的网络列表。</summary>
    public void OpenNetwork()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-availablenetworks:",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开网络面板失败：{ex.Message}");
        }
    }

    /// <summary>点击音量图标：切换静音（发送系统静音键）。</summary>
    public void ToggleMute()
    {
        try
        {
            NativeMethods.keybd_event(0xAD, 0, 0, UIntPtr.Zero);   // VK_VOLUME_MUTE
            NativeMethods.keybd_event(0xAD, 0, 2, UIntPtr.Zero);
            AppLog.Info("托盘：切换静音");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"切换静音失败：{ex.Message}");
        }
    }

    public void OpenBluetoothSettings()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "ms-settings:bluetooth",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开蓝牙设置失败：{ex.Message}");
        }
    }

    /// <summary>点击输入法：在已安装的输入语言之间切换。</summary>
    public void CycleInputLanguage()
    {
        try
        {
            var manager = InputLanguageManager.Current;
            var languages = manager.AvailableInputLanguages?.Cast<CultureInfo>().ToList() ?? new List<CultureInfo>();
            if (languages.Count < 2)
            {
                return;
            }

            var current = manager.CurrentInputLanguage;
            var index = languages.FindIndex(l => string.Equals(l.Name, current.Name, StringComparison.OrdinalIgnoreCase));
            var next = languages[(index + 1) % languages.Count];
            manager.CurrentInputLanguage = next;
            AppLog.Info($"托盘：输入法切换到 {next.DisplayName}");
            UpdateIme();
        }
        catch (Exception ex)
        {
            AppLog.Warn($"切换输入法失败：{ex.Message}");
        }
    }

    private void UpdateNetwork()
    {
        try
        {
            var interfaces = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up
                            && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .ToList();

            var wifi = interfaces.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211);
            var wired = interfaces.FirstOrDefault(n => n.NetworkInterfaceType == NetworkInterfaceType.Ethernet);

            if (wifi is not null)
            {
                WifiGlyph = "\uE701";   // WiFi
                WifiOpacity = 1.0;
                WifiTooltip = $"Wi-Fi 已连接：{wifi.Name}";
            }
            else if (wired is not null)
            {
                WifiGlyph = "\uE839";   // 有线
                WifiOpacity = 1.0;
                WifiTooltip = $"有线网络：{wired.Name}";
            }
            else
            {
                WifiGlyph = "\uE701";
                WifiOpacity = 0.4;
                WifiTooltip = "未连接网络";
            }
        }
        catch
        {
            // 查询失败时保持原样
        }
    }

    private void UpdateIme()
    {
        try
        {
            var culture = InputLanguageManager.Current.CurrentInputLanguage;
            ImeText = culture.TwoLetterISOLanguageName.Equals("zh", StringComparison.OrdinalIgnoreCase) ? "中" : "EN";
        }
        catch
        {
            ImeText = "EN";
        }
    }

    private RunningAppViewModel? FindRunning(string key)
    {
        foreach (var item in Running)
        {
            if (string.Equals(item.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                return item;
            }
        }

        return null;
    }

    private void RefreshHasItems() => HasItems = Items.Count > 0;
}
