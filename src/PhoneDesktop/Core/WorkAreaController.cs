using PhoneDesktop.Interop;

namespace PhoneDesktop.Core;

/// <summary>
/// 隐藏任务栏后，Windows 的工作区仍然停留在“任务栏之上”，
/// 最大化窗口会在屏幕底部留下一条空白（40px 左右）。
/// 这里把工作区扩到整屏，退出时再恢复；恢复时优先按任务栏实际位置计算，
/// 避免异常退出后把错误的工作区当成“原始值”。
/// </summary>
public static class WorkAreaController
{
    private static NativeMethods.RECT _saved;
    private static bool _expanded;

    public static bool IsExpanded => _expanded;

    public static int ScreenWidth => NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);

    public static int ScreenHeight => NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);

    public static bool ExpandToFullScreen()
    {
        if (_expanded)
        {
            return true;
        }

        try
        {
            var current = new NativeMethods.RECT();
            if (!NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref current, 0))
            {
                AppLog.Warn("读取工作区失败，跳过硬屏铺满");
                return false;
            }

            _saved = current;

            var full = new NativeMethods.RECT
            {
                Left = 0,
                Top = 0,
                Right = ScreenWidth,
                Bottom = ScreenHeight,
            };
            // 不能广播 WM_SETTINGCHANGE：Explorer 收到后会按任务栏的 appbar 预留把工作区改回去
            var ok = NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETWORKAREA, 0, ref full, 0);

            _expanded = ok;
            AppLog.Info(ok
                ? $"工作区已扩到整屏：{current.Width}x{current.Height} → {ScreenWidth}x{ScreenHeight}"
                : "工作区扩展失败（最大化窗口仍会留出任务栏高度的空白）");
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"工作区扩展异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>恢复工作区：优先用任务栏当前矩形推算，任务栏找不到时退回启动时保存的值。</summary>
    public static bool Restore()
    {
        if (!_expanded)
        {
            return false;
        }

        try
        {
            var target = ComputeFromTaskbar();
            // 恢复时广播一次，让已最大化的窗口重新按工作区排版
            var ok = NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETWORKAREA, 0, ref target, NativeMethods.SPIF_SENDCHANGE);
            _expanded = false;
            AppLog.Info(ok
                ? $"工作区已恢复：{target.Width}x{target.Height}"
                : $"工作区恢复失败，沿用原值 {_saved.Width}x{_saved.Height}");
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"工作区恢复异常：{ex.Message}");
            return false;
        }
    }

    /// <summary>按任务栏实际位置强制校正工作区（不依赖本次进程是否扩展过）。</summary>
    public static bool ForceRestore()
    {
        try
        {
            var target = ComputeFromTaskbar();
            var ok = NativeMethods.SystemParametersInfo(
                NativeMethods.SPI_SETWORKAREA, 0, ref target, NativeMethods.SPIF_SENDCHANGE);
            _expanded = false;
            AppLog.Info(ok
                ? $"工作区已按任务栏校正：{target.Width}x{target.Height}"
                : "工作区校正失败");
            return ok;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"工作区校正异常：{ex.Message}");
            return false;
        }
    }

    private static NativeMethods.RECT ComputeFromTaskbar()
    {
        var screenWidth = ScreenWidth;
        var screenHeight = ScreenHeight;

        var taskbar = NativeMethods.FindWindow("Shell_TrayWnd", IntPtr.Zero);
        if (taskbar == IntPtr.Zero || !NativeMethods.GetWindowRect(taskbar, out var rect))
        {
            return _saved.Width > 0 ? _saved : new NativeMethods.RECT { Right = screenWidth, Bottom = screenHeight };
        }

        // 任务栏贴着哪条边，就从哪条边扣掉它的厚度
        if (rect.Top <= 0 && rect.Bottom >= screenHeight)
        {
            return new NativeMethods.RECT { Right = screenWidth, Bottom = rect.Top };   // 顶部任务栏
        }

        if (rect.Left <= 0 && rect.Right >= screenWidth)
        {
            return new NativeMethods.RECT { Right = rect.Right, Bottom = screenHeight }; // 左侧/右侧竖排
        }

        return new NativeMethods.RECT { Right = screenWidth, Bottom = rect.Top };        // 默认底部
    }
}
