using System.Windows.Input;
using PhoneDesktop.Core;
using PhoneDesktop.Interop;
using PhoneDesktop.Models;

namespace PhoneDesktop.Services;

/// <summary>
/// 全局快捷键的持有者：负责解析设置里的组合键、注册、改键时先注销再注册。
/// 默认 Ctrl+Space 在中文输入法下常被占用，注册失败要把原因带回给调用方提示用户。
/// </summary>
public sealed class HotkeyManager : IDisposable
{
    private readonly HotkeyWindow _window = new();
    private readonly Action _onPressed;

    private int _currentId = -1;
    private bool _disposed;

    public HotkeyManager(Action onPressed)
    {
        _onPressed = onPressed;
    }

    public string? LastError { get; private set; }

    public bool IsRegistered => _currentId >= 0;

    public string CurrentDisplay { get; private set; } = string.Empty;

    public bool TryApply(HotkeySettings settings, out string? error)
    {
        error = null;

        if (!TryParse(settings, out var modifiers, out var key, out var parseError))
        {
            LastError = parseError;
            error = parseError;
            return false;
        }

        // 先注册新组合键，成功后再注销旧的：这样新键注册失败时旧键仍然可用
        if (!_window.TryRegister(modifiers, key, _onPressed, out var id, out var registerError))
        {
            LastError = registerError;
            error = registerError;
            AppLog.Warn($"全局快捷键注册失败（{HotkeyDisplay.Describe(modifiers, key)}）：{registerError}");
            return false;
        }

        if (_currentId >= 0)
        {
            _window.Unregister(_currentId);
        }

        _currentId = id;
        LastError = null;
        CurrentDisplay = HotkeyDisplay.Describe(modifiers, key);
        AppLog.Info($"全局快捷键已注册：{CurrentDisplay}");
        return true;
    }

    public static bool TryParse(HotkeySettings settings, out ModifierKeys modifiers, out Key key, out string? error)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        error = null;

        if (settings is null || string.IsNullOrWhiteSpace(settings.Key))
        {
            error = "未配置快捷键";
            return false;
        }

        if (!Enum.TryParse<Key>(settings.Key, ignoreCase: true, out key) || key == Key.None)
        {
            error = $"无法识别的按键：{settings.Key}";
            return false;
        }

        var parts = (settings.Modifiers ?? string.Empty)
            .Split(new[] { '+', ',', ' ' }, StringSplitOptions.RemoveEmptyEntries);

        foreach (var part in parts)
        {
            if (!Enum.TryParse<ModifierKeys>(part.Trim(), ignoreCase: true, out var modifier))
            {
                error = $"无法识别的修饰键：{part}";
                return false;
            }

            modifiers |= modifier;
        }

        if (modifiers == ModifierKeys.None && key is >= Key.A and <= Key.Z)
        {
            // 纯字母键做全局热键会干扰正常输入，要求至少带一个修饰键
            error = "全局快捷键至少需要一个修饰键（Ctrl / Alt / Shift / Win）";
            return false;
        }

        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_currentId >= 0)
        {
            _window.Unregister(_currentId);
            _currentId = -1;
        }

        _window.Dispose();
    }
}

public static class HotkeyDisplay
{
    public static string Describe(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            parts.Add("Ctrl");
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
            parts.Add("Win");
        }

        parts.Add(DescribeKey(key));
        return string.Join(" + ", parts);
    }

    private static string DescribeKey(Key key) => key switch
    {
        Key.Space => "Space",
        Key.Return => "Enter",
        Key.Escape => "Esc",
        Key.Oem3 => "`",
        Key.OemMinus => "-",
        Key.OemPlus => "=",
        _ => key.ToString(),
    };
}
