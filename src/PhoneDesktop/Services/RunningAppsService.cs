using System.Diagnostics;
using System.IO;
using System.Text;
using PhoneDesktop.Core;
using PhoneDesktop.Interop;

namespace PhoneDesktop.Services;

/// <summary>一个正在运行的应用（按 exe 归组，代表窗口用于激活/最小化）。</summary>
public sealed class RunningApp
{
    public required string Key { get; init; }

    public required string Title { get; init; }

    public required IntPtr Hwnd { get; init; }

    public required string ExecutablePath { get; init; }

    public bool IsMinimized { get; init; }

    public bool IsActive { get; init; }
}

/// <summary>
/// 枚举“用户看得见的顶层窗口”，供 Dock 当作任务栏使用。
/// 刻意跳过工具窗口、被 DWM 隐藏的窗口（虚拟桌面）、我们自己的窗口和桌面壳层。
/// </summary>
public sealed class RunningAppsService
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    private static readonly HashSet<string> IgnoredClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",
        "WorkerW",
        "Shell_TrayWnd",
        "Shell_SecondaryTrayWnd",
        "Windows.UI.Core.CoreWindow",
        "ApplicationManager_DesktopShellWindow",
        "PhoneDesktopDrawer",
        "PhoneDesktopOverlay",
        "PhoneDesktopSettings",
        "TaskListThumbnailWnd",
        "ForegroundStaging",
    };

    private readonly uint _ownProcessId = (uint)Environment.ProcessId;

    public IReadOnlyList<RunningApp> Snapshot()
    {
        var foreground = NativeMethods.GetAncestor(NativeMethods.GetForegroundWindow(), 2);  // GA_ROOT
        var candidates = new Dictionary<string, (RunningApp App, int Score)>(StringComparer.OrdinalIgnoreCase);

        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!TryDescribe(hwnd, foreground, out var app, out var score))
            {
                return true;
            }

            if (candidates.TryGetValue(app.Key, out var existing))
            {
                if (score > existing.Score)
                {
                    candidates[app.Key] = (app, score);
                }
            }
            else
            {
                candidates[app.Key] = (app, score);
            }

            return true;
        }, IntPtr.Zero);

        return candidates.Values
            .Select(entry => entry.App)
            .OrderBy(app => app.Title, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    public bool Toggle(RunningApp app)
    {
        if (app.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(app.Hwnd))
        {
            return false;
        }

        try
        {
            if (NativeMethods.IsIconic(app.Hwnd))
            {
                NativeMethods.ShowWindow(app.Hwnd, NativeMethods.SW_RESTORE);
                ForceForeground(app.Hwnd);
                AppLog.Info($"运行项：恢复 {app.Title}");
                return true;
            }

            if (app.IsActive)
            {
                NativeMethods.ShowWindow(app.Hwnd, NativeMethods.SW_MINIMIZE);
                AppLog.Info($"运行项：最小化 {app.Title}");
                return true;
            }

            ForceForeground(app.Hwnd);
            AppLog.Info($"运行项：切换到 {app.Title}");
            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"运行项操作失败（{app.Title}）：{ex.Message}");
            return false;
        }
    }

    public bool Close(RunningApp app)
    {
        if (app.Hwnd == IntPtr.Zero || !NativeMethods.IsWindow(app.Hwnd))
        {
            return false;
        }

        NativeMethods.PostMessage(app.Hwnd, NativeMethods.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        AppLog.Info($"运行项：请求关闭 {app.Title}");
        return true;
    }

    private bool TryDescribe(IntPtr hwnd, IntPtr foreground, out RunningApp app, out int score)
    {
        app = null!;
        score = 0;

        if (!NativeMethods.IsWindowVisible(hwnd))
        {
            return false;
        }

        var length = NativeMethods.GetWindowTextLength(hwnd);
        if (length <= 0)
        {
            return false;
        }

        var className = NativeMethods.ClassNameOf(hwnd);
        if (IgnoredClasses.Contains(className))
        {
            return false;
        }

        var exStyle = NativeMethods.GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        if ((exStyle & WS_EX_TOOLWINDOW) != 0)
        {
            return false;
        }

        if (NativeMethods.GetWindow(hwnd, NativeMethods.GW_OWNER) != IntPtr.Zero)
        {
            return false;
        }

        // 虚拟桌面里不可见的窗口会被 DWM 标记为 cloaked
        if (NativeMethods.DwmGetWindowAttribute(hwnd, NativeMethods.DWMWA_CLOAKED, out var cloaked, sizeof(int)) == 0
            && cloaked != 0)
        {
            return false;
        }

        NativeMethods.GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == _ownProcessId || processId == 0)
        {
            return false;
        }

        var title = new StringBuilder(length + 1);
        NativeMethods.GetWindowText(hwnd, title, title.Capacity);
        var caption = title.ToString().Trim();
        if (caption.Length == 0)
        {
            return false;
        }

        var exePath = ResolveExecutable(processId, out var processName);
        var isUwp = string.Equals(processName, "ApplicationFrameHost", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(processName, "WWAHost", StringComparison.OrdinalIgnoreCase);

        // UWP 都是 ApplicationFrameHost 宿主，只能按标题区分
        var key = isUwp ? $"uwp:{caption}" : (exePath.Length > 0 ? exePath : $"name:{processName}");

        var isMinimized = NativeMethods.IsIconic(hwnd);
        var isActive = foreground == hwnd;

        app = new RunningApp
        {
            Key = key,
            Title = caption,
            Hwnd = hwnd,
            ExecutablePath = exePath,
            IsMinimized = isMinimized,
            IsActive = isActive,
        };

        score = (isActive ? 4 : 0) + (isMinimized ? 1 : 2);
        return true;
    }

    private static string ResolveExecutable(uint processId, out string processName)
    {
        processName = string.Empty;
        try
        {
            using var process = Process.GetProcessById((int)processId);
            processName = process.ProcessName;
            try
            {
                return process.MainModule?.FileName ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void ForceForeground(IntPtr hwnd)
    {
        if (NativeMethods.SetForegroundWindow(hwnd))
        {
            return;
        }

        // 前台锁定时用 AttachThreadInput 兜底
        var foreground = NativeMethods.GetForegroundWindow();
        var foregroundThread = NativeMethods.GetWindowThreadProcessId(foreground, out _);
        var targetThread = NativeMethods.GetWindowThreadProcessId(hwnd, out _);
        if (foregroundThread == targetThread)
        {
            NativeMethods.SetForegroundWindow(hwnd);
            return;
        }

        if (NativeMethods.AttachThreadInput(targetThread, foregroundThread, true))
        {
            NativeMethods.SetForegroundWindow(hwnd);
            NativeMethods.AttachThreadInput(targetThread, foregroundThread, false);
        }
    }
}
