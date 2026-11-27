using System.Diagnostics;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using PhoneDesktop.Core;
using PhoneDesktop.Interop;
using PhoneDesktop.Models;
using PhoneDesktop.Overlay;
using PhoneDesktop.Services;
using PhoneDesktop.Settings;

namespace PhoneDesktop;

public partial class App : Application
{
    private static readonly Stopwatch StartupWatch = Stopwatch.StartNew();

    private SingleInstance? _singleInstance;
    private DesktopHost? _desktop;
    private OverlayHost? _overlay;
    private HotkeyWindow? _hotkeys;
    private HotkeyManager? _globalHotkeys;
    private SettingsWindow? _settingsWindow;
    private SettingsService? _settingsService;
    private bool _exiting;

    /// <summary>--dev 模式下额外提供调试角标与 Ctrl+Alt+Q 退出。</summary>
    public static bool IsDevMode { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        AppLog.Init();
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            TaskbarController.Restore();
            WorkAreaController.Restore();
        };
        SessionEnding += (_, _) =>
        {
            TaskbarController.Restore();
            WorkAreaController.Restore();
        };

        DispatcherUnhandledException += (_, args) =>
        {
            AppLog.Error("UI 线程未处理异常", args.Exception);
            TaskbarController.Restore();
            ExitApplication(exitCode: 1);
        };

        try
        {
            if (HasArgument(e.Args, "--restore-taskbar"))
            {
                var restored = TaskbarController.Restore(force: true);
                AppLog.Info($"收到 --restore-taskbar：{(restored ? "已恢复任务栏" : "任务栏未隐藏或恢复失败")}");
                WorkAreaController.ForceRestore();

                // 透明模式下异常退出可能把桌面图标留在隐藏状态
                try
                {
                    var desktop = new DesktopHost();
                    if (desktop.TryLocate())
                    {
                        desktop.SetDesktopIconsVisible(true);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"恢复桌面图标失败：{ex.Message}");
                }
                Shutdown(0);
                return;
            }

            if (HasArgument(e.Args, "--exit"))
            {
                var signaled = SingleInstance.TrySignalExit();
                AppLog.Info($"收到 --exit：{(signaled ? "已通知运行中的实例退出" : "没有运行中的实例")}");
                Shutdown(0);
                return;
            }

            _singleInstance = new SingleInstance();
            if (!_singleInstance.IsFirstInstance)
            {
                AppLog.Info("已有实例在运行，本实例退出");
                Shutdown(0);
                return;
            }

            _singleInstance.ExitRequested += OnExitRequested;

            IsDevMode = HasArgument(e.Args, "--dev");

            _settingsService = new SettingsService();
            AppLog.Info($"配置：{_settingsService.FilePath}（兜底色 {_settingsService.Settings.FallbackColor}）");

            // 手机风格桌面不显示 Windows 任务栏：运行期间隐藏，退出恢复
            TaskbarController.Hide();

            // 任务栏隐藏后工作区仍停在“任务栏之上”，最大化窗口会留一条空白；
            // 这里把工作区扩到整屏，退出时恢复（Windows 工作区是全局设置）
            WorkAreaController.ExpandToFullScreen();

            _desktop = new DesktopHost();
            var transparentDesktop = _settingsService.Settings.UseTransparentDesktop
                                     || Environment.GetEnvironmentVariable("PHONEDESKTOP_TRANSPARENT") == "1";
            _overlay = new OverlayHost(
                _desktop,
                new WallpaperService(),
                _settingsService,
                new OverlayOptions(ShowDebugBadge: IsDevMode, TransparentDesktop: transparentDesktop));
            AppLog.Info($"桌面模式：{(transparentDesktop ? "透明（动态壁纸可见）" : "覆盖层")}");
            _overlay.ExitRequested += (_, _) => ExitApplication(0);

            _overlay.Attach();
            _overlay.SettingsRequested += (_, _) => OpenSettings();

            AppLog.Info($"渲染级别：Tier {RenderCapability.Tier >> 16}");

            // 开机自启：把设置同步到注册表（首次运行为开启）
            AutostartService.SetEnabled(_settingsService.Settings.RunAtStartup);

            RegisterGlobalHotkey();

            if (IsDevMode)
            {
                RegisterDevHotkey();
            }

            AppLog.Info($"启动完成：{StartupWatch.ElapsedMilliseconds} ms（自托管启动计时）");
        }
        catch (Exception ex)
        {
            AppLog.Error("启动失败", ex);
            ExitApplication(exitCode: 1);
        }
    }

    private void RegisterGlobalHotkey()
    {
        try
        {
            _globalHotkeys = new HotkeyManager(() => _overlay?.ShowDrawer());

            // 测试钩子：允许用环境变量覆盖默认快捷键（自动化验收用，不影响正常配置）
            var overrideValue = Environment.GetEnvironmentVariable("PHONEDESKTOP_HOTKEY");
            var settings = _settingsService!.Settings.Hotkey;
            if (!string.IsNullOrWhiteSpace(overrideValue))
            {
                var parts = overrideValue.Split('+', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                {
                    settings = new HotkeySettings
                    {
                        Modifiers = string.Join("+", parts[..^1]),
                        Key = parts[^1].Trim(),
                    };
                    AppLog.Info($"全局快捷键被环境变量覆盖：{overrideValue}");
                }
            }

            if (_globalHotkeys.TryApply(settings, out var error))
            {
                return;
            }

            var message = $"全局快捷键注册失败：{error}（可在设置中更换）";
            AppLog.Warn(message);
            _overlay?.ShowToast(message);

            // 正常模式下给一次性提示；调试模式下只写日志，避免对话框挡住自动化验收
            if (!IsDevMode)
            {
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    var result = MessageBox.Show(
                        $"{message}\n\n是否现在打开设置更换快捷键？",
                        "PhoneDesktop",
                        MessageBoxButton.YesNo,
                        MessageBoxImage.Warning);
                    if (result == MessageBoxResult.Yes)
                    {
                        OpenSettings();
                    }
                }));
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("全局快捷键初始化失败", ex);
        }
    }

    private void OpenSettings()
    {
        try
        {
            if (_settingsWindow is { IsVisible: true })
            {
                _settingsWindow.Activate();
                return;
            }

            if (_settingsService is null || _globalHotkeys is null)
            {
                return;
            }

            _settingsWindow = new SettingsWindow(_settingsService, _globalHotkeys, () => _overlay?.RefreshBackdrop());
            _settingsWindow.Closed += (_, _) => _settingsWindow = null;
            _settingsWindow.Show();
            _settingsWindow.Activate();
            AppLog.Info("设置页已打开");
        }
        catch (Exception ex)
        {
            AppLog.Error("打开设置页失败", ex);
        }
    }

    private void RegisterDevHotkey()
    {
        try
        {
            _hotkeys = new HotkeyWindow();
            if (_hotkeys.Register(ModifierKeys.Control | ModifierKeys.Alt, Key.Q, () => ExitApplication(0), out var error))
            {
                AppLog.Info("调试退出快捷键 Ctrl+Alt+Q 已注册");
            }
            else
            {
                AppLog.Warn($"调试退出快捷键 Ctrl+Alt+Q 注册失败：{error}（可用 PhoneDesktop.exe --exit 退出）");
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("调试快捷键窗口创建失败", ex);
        }
    }

    private void OnExitRequested()
    {
        Dispatcher.BeginInvoke(new Action(() => ExitApplication(0)));
    }

    private void ExitApplication(int exitCode)
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;
        AppLog.Info("开始退出：销毁覆盖层并释放资源");

        TaskbarController.Restore();

        try
        {
            _hotkeys?.Dispose();
            _hotkeys = null;

            _globalHotkeys?.Dispose();
            _globalHotkeys = null;

            _settingsWindow?.Close();
            _settingsWindow = null;

            _overlay?.Dispose();
            _overlay = null;

            if (_singleInstance is not null)
            {
                _singleInstance.ExitRequested -= OnExitRequested;
                _singleInstance.Dispose();
                _singleInstance = null;
            }
        }
        catch (Exception ex)
        {
            AppLog.Error("退出清理时出错", ex);
        }

        AppLog.Info("已退出，原生桌面恢复");
        Shutdown(exitCode);
    }

    private static bool HasArgument(IEnumerable<string> args, string expected)
        => args.Any(arg => string.Equals(arg, expected, StringComparison.OrdinalIgnoreCase));
}
