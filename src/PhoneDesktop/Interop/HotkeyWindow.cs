using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;

namespace PhoneDesktop.Interop;

/// <summary>
/// 全局快捷键宿主：一个不显示的顶层窗口，接收 WM_HOTKEY。
/// 阶段 1 只用来注册开发期的退出键，阶段 4 复用于抽屉快捷键。
/// </summary>
public sealed class HotkeyWindow : IDisposable
{
    private readonly HwndSource _source;
    private readonly Dictionary<int, Action> _callbacks = new();
    private int _nextId;
    private bool _disposed;

    public HotkeyWindow()
    {
        var parameters = new HwndSourceParameters("PhoneDesktopHotkeySink")
        {
            ParentWindow = IntPtr.Zero,
            WindowStyle = NativeMethods.WS_POPUP,
            ExtendedWindowStyle = 0,
            PositionX = 0,
            PositionY = 0,
            Width = 1,
            Height = 1,
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);
    }

    public IntPtr Handle => _source.Handle;

    /// <summary>注册全局快捷键；失败时给出系统错误原因（用于“注册失败提示用户”）。</summary>
    public bool Register(ModifierKeys modifiers, Key key, Action action, out string? error)
        => TryRegister(modifiers, key, action, out _, out error);

    /// <summary>注册全局快捷键并返回 id（用于之后注销）。</summary>
    public bool TryRegister(ModifierKeys modifiers, Key key, Action action, out int id, out string? error)
    {
        error = null;
        id = 0;

        if (key == Key.None)
        {
            error = "未指定按键";
            return false;
        }

        id = Interlocked.Increment(ref _nextId);
        var nativeModifiers = ToNative(modifiers) | NativeMethods.MOD_NOREPEAT;
        var virtualKey = (uint)KeyInterop.VirtualKeyFromKey(key);

        if (!NativeMethods.RegisterHotKey(_source.Handle, id, nativeModifiers, virtualKey))
        {
            var code = Marshal.GetLastWin32Error();
            error = code == 1409
                ? "该快捷键已被其他程序占用"
                : new Win32Exception(code).Message;
            return false;
        }

        _callbacks[id] = action;
        return true;
    }

    public bool Unregister(int id)
    {
        if (!_callbacks.Remove(id))
        {
            return false;
        }

        return NativeMethods.UnregisterHotKey(_source.Handle, id);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == NativeMethods.WM_HOTKEY && _callbacks.TryGetValue(wParam.ToInt32(), out var callback))
        {
            handled = true;
            callback();
        }

        return IntPtr.Zero;
    }

    private static uint ToNative(ModifierKeys modifiers)
    {
        uint result = 0;

        if ((modifiers & ModifierKeys.Alt) != 0)
        {
            result |= NativeMethods.MOD_ALT;
        }

        if ((modifiers & ModifierKeys.Control) != 0)
        {
            result |= NativeMethods.MOD_CONTROL;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            result |= NativeMethods.MOD_SHIFT;
        }

        if ((modifiers & ModifierKeys.Windows) != 0)
        {
            result |= NativeMethods.MOD_WIN;
        }

        return result;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        foreach (var id in _callbacks.Keys.ToArray())
        {
            try
            {
                NativeMethods.UnregisterHotKey(_source.Handle, id);
            }
            catch
            {
                // 忽略
            }
        }

        _callbacks.Clear();

        try
        {
            _source.RemoveHook(WndProc);
            _source.Dispose();
        }
        catch
        {
            // 忽略
        }
    }
}
