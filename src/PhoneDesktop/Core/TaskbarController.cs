using PhoneDesktop.Interop;

namespace PhoneDesktop.Core;

/// <summary>
/// Windows 任务栏的显隐控制。
///
/// 任务栏（Shell_TrayWnd）是系统里最上层的 shell 窗口，按需求“不修改系统 Shell”，
/// 我们不改它的任何设置，只在运行期间隐藏它、退出时恢复——否则手机风格桌面底部
/// 会一直露着 Windows 任务栏。
///
/// 兜底：
///   - 正常退出 / 未处理异常 / 会话结束都会恢复；
///   - 万一被强杀，可运行 `PhoneDesktop.exe --restore-taskbar` 立即恢复。
/// </summary>
public static class TaskbarController
{
    private const string TaskbarClass = "Shell_TrayWnd";

    private static bool _hidden;

    public static bool IsHidden => _hidden;

    public static bool Hide()
    {
        var taskbar = NativeMethods.FindWindow(TaskbarClass, IntPtr.Zero);
        if (taskbar == IntPtr.Zero)
        {
            AppLog.Warn("未找到 Windows 任务栏窗口，跳过隐藏");
            return false;
        }

        // ShowWindow 的返回值是“调用前是否可见”，不能当成功标志；用 IsWindowVisible 复核
        NativeMethods.ShowWindow(taskbar, NativeMethods.SW_HIDE);
        if (NativeMethods.IsWindowVisible(taskbar))
        {
            AppLog.Warn("隐藏 Windows 任务栏失败");
            return false;
        }

        _hidden = true;
        AppLog.Info("已隐藏 Windows 任务栏（退出时恢复）");
        return true;
    }

    public static bool Restore(bool force = false)
    {
        if (!force && !_hidden)
        {
            return false;
        }

        var taskbar = NativeMethods.FindWindow(TaskbarClass, IntPtr.Zero);
        if (taskbar == IntPtr.Zero)
        {
            _hidden = false;
            return false;
        }

        NativeMethods.ShowWindow(taskbar, NativeMethods.SW_SHOW);
        var ok = NativeMethods.IsWindowVisible(taskbar);
        _hidden = false;
        AppLog.Info(ok ? "已恢复 Windows 任务栏" : "恢复 Windows 任务栏失败");
        return ok;
    }
}
