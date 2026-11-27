using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using PhoneDesktop.Core;
using PhoneDesktop.Interop;
using PhoneDesktop.Models;
using PhoneDesktop.Services;

namespace PhoneDesktop.Settings;

public partial class SettingsWindow : Window
{
    private const string DefaultModifiers = "Control";
    private const string DefaultKey = "Space";

    private static readonly string[] Palette =
    {
        "#FF1B1B1F",
        "#FF0E0F13",
        "#FF2B2D3A",
        "#FF3B2F4A",
        "#FF1F3A34",
        "#FF3A2B1F",
        "#FF40202A",
        "#FFF2E9D0",
    };

    private readonly SettingsService _settingsService;
    private readonly HotkeyManager _hotkeys;
    private readonly Action _onFallbackColorChanged;

    private bool _loading;
    private bool _capturing;

    public SettingsWindow(SettingsService settingsService, HotkeyManager hotkeys, Action onFallbackColorChanged)
    {
        InitializeComponent();

        _settingsService = settingsService;
        _hotkeys = hotkeys;
        _onFallbackColorChanged = onFallbackColorChanged;

        Loaded += (_, _) => LoadValues();
        PreviewKeyDown += OnPreviewKeyDown;
        SourceInitialized += (_, _) => ApplyToolWindowStyle();
    }

    private AppSettings Settings => _settingsService.Settings;

    private void LoadValues()
    {
        _loading = true;

        AutostartCheck.IsChecked = Settings.RunAtStartup;
        TransparentCheck.IsChecked = Settings.UseTransparentDesktop;
        TransparentHint.Text = Settings.UseTransparentDesktop
            ? "已开启：隐藏桌面图标、动态壁纸直接可见（重启后生效）"
            : "隐藏桌面图标，让 Wallpaper Engine 等动态壁纸直接显示；重启后生效";
        HotkeyText.Text = DescribeConfiguredHotkey();
        UpdateHotkeyStatus();
        BuildColorSwatches();

        AboutText.Text =
            $"PhoneDesktop {GetVersion()}\n"
            + $"{RuntimeInformation.FrameworkDescription}\n"
            + $"配置：{_settingsService.FilePath}\n"
            + $"日志：{AppLog.LogPath}";
        VersionText.Text = $"版本 {GetVersion()}";

        _loading = false;
    }

    private void OnAutostartChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = AutostartCheck.IsChecked == true;
        Settings.RunAtStartup = enabled;
        var applied = AutostartService.SetEnabled(enabled);
        _settingsService.Save();
        AppLog.Info($"设置：开机自启 = {enabled}（{(applied ? "已写入注册表" : "写入失败")}）");
    }

    private void OnTransparentChanged(object sender, RoutedEventArgs e)
    {
        if (_loading)
        {
            return;
        }

        var enabled = TransparentCheck.IsChecked == true;
        Settings.UseTransparentDesktop = enabled;
        _settingsService.Save();
        TransparentHint.Text = enabled
            ? "已开启：隐藏桌面图标、动态壁纸直接可见（**重启后生效**）"
            : "隐藏桌面图标，让 Wallpaper Engine 等动态壁纸直接显示；重启后生效";
        AppLog.Info($"设置：透明桌面模式 = {enabled}（重启后生效）");
    }

    private void OnHotkeyBoxClick(object sender, MouseButtonEventArgs e)
    {
        _capturing = true;
        HotkeyText.Text = "请按下新的组合键…";
        HotkeyHint.Text = "按 Esc 取消";
        Keyboard.Focus(HotkeyBox);
        e.Handled = true;
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing)
        {
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                Close();
            }

            return;
        }

        if (e.Key == Key.Escape)
        {
            _capturing = false;
            HotkeyText.Text = DescribeConfiguredHotkey();
            HotkeyHint.Text = "点击上面的框，然后按下新的组合键（Esc 取消）";
            e.Handled = true;
            return;
        }

        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt
            or Key.LeftShift or Key.RightShift
            or Key.LWin or Key.RWin
            or Key.None)
        {
            e.Handled = true;
            return;
        }

        var modifiers = Keyboard.Modifiers;
        if (modifiers == ModifierKeys.None && key is >= Key.A and <= Key.Z)
        {
            HotkeyStatus.Text = "全局快捷键至少需要一个修饰键（Ctrl / Alt / Shift / Win）";
            e.Handled = true;
            return;
        }

        TryApplyHotkey(modifiers, key);
        e.Handled = true;
    }

    private void TryApplyHotkey(ModifierKeys modifiers, Key key)
    {
        var candidate = new HotkeySettings
        {
            Modifiers = DescribeModifiers(modifiers),
            Key = key.ToString(),
        };

        _capturing = false;

        if (_hotkeys.TryApply(candidate, out var error))
        {
            Settings.Hotkey = candidate;
            _settingsService.Save();
            HotkeyText.Text = _hotkeys.CurrentDisplay;
            HotkeyHint.Text = "点击上面的框，然后按下新的组合键（Esc 取消）";
            UpdateHotkeyStatus();
            return;
        }

        HotkeyText.Text = DescribeConfiguredHotkey();
        HotkeyHint.Text = "换一个组合键再试";
        HotkeyStatus.Text = $"注册失败：{error}";
    }

    private void OnResetHotkeyClick(object sender, RoutedEventArgs e)
    {
        var defaults = new HotkeySettings { Modifiers = DefaultModifiers, Key = DefaultKey };

        if (_hotkeys.TryApply(defaults, out var error))
        {
            Settings.Hotkey = defaults;
            _settingsService.Save();
            HotkeyText.Text = _hotkeys.CurrentDisplay;
            UpdateHotkeyStatus();
            HotkeyHint.Text = "已恢复默认：Ctrl + Space";
            return;
        }

        HotkeyStatus.Text = $"默认快捷键注册失败：{error}";
    }

    private void UpdateHotkeyStatus()
    {
        if (_hotkeys.IsRegistered)
        {
            HotkeyStatus.Text = $"状态：已注册（{_hotkeys.CurrentDisplay}）";
            HotkeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x7C, 0xD9, 0x9A));
            return;
        }

        HotkeyStatus.Text = $"状态：注册失败{(string.IsNullOrEmpty(_hotkeys.LastError) ? string.Empty : $"（{_hotkeys.LastError}）")}";
        HotkeyStatus.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8A, 0x8A));
    }

    private void BuildColorSwatches()
    {
        ColorPanel.Children.Clear();

        var current = Settings.ResolveFallbackColor();

        foreach (var hex in Palette)
        {
            var color = (Color)ColorConverter.ConvertFromString(hex);
            var swatch = new Border
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 10, 0),
                CornerRadius = new CornerRadius(10),
                Background = new SolidColorBrush(color),
                BorderBrush = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF)),
                BorderThickness = new Thickness(1),
                Cursor = Cursors.Hand,
                Tag = hex,
                ToolTip = hex,
            };

            if (color == current)
            {
                swatch.BorderThickness = new Thickness(3);
            }

            swatch.MouseLeftButtonUp += OnColorClick;
            ColorPanel.Children.Add(swatch);
        }

        ColorStatus.Text = $"当前：#{current.R:X2}{current.G:X2}{current.B:X2}（点击色块切换）";
    }

    private void OnColorClick(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Border { Tag: string hex })
        {
            return;
        }

        Settings.FallbackColor = hex;
        _settingsService.Save();
        AppLog.Info($"设置：兜底色 = {hex}");
        BuildColorSwatches();
        _onFallbackColorChanged();
        e.Handled = true;
    }

    private void OnOpenConfigFolderClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{_settingsService.DirectoryPath}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            AppLog.Warn($"打开配置文件夹失败：{ex.Message}");
        }
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 拖动失败不影响其它操作
            }
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void ApplyToolWindowStyle()
    {
        try
        {
            var handle = new WindowInteropHelper(this).Handle;
            var exStyle = NativeMethods.GetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE).ToInt64();
            NativeMethods.SetWindowLongPtr(handle, NativeMethods.GWL_EXSTYLE, new IntPtr(exStyle | 0x00000080));
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置窗口样式失败：{ex.Message}");
        }
    }

    private string DescribeConfiguredHotkey()
        => HotkeyManager.TryParse(Settings.Hotkey, out var modifiers, out var key, out _)
            ? HotkeyDisplay.Describe(modifiers, key)
            : Settings.Hotkey?.Key ?? "未设置";

    private static string DescribeModifiers(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Control");
        }

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            parts.Add("Alt");
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            parts.Add("Shift");
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            parts.Add("Windows");
        }

        return string.Join("+", parts);
    }

    private static string GetVersion()
        => Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0";
}
