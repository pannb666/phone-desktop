using PhoneDesktop.Core;

namespace PhoneDesktop.Interop;

/// <summary>
/// 桌面窗口（Progman / WorkerW）定位与 z 序维护。
///
/// 层级模型的核心在这里：
///   覆盖层是“桌面图标层宿主”的 WS_CHILD 子窗口，并被排到该宿主子窗口 z 序的最上方。
///   → 结构性地“盖住桌面图标”，同时“天然位于所有顶层窗口之下”。
///
/// 注意：桌面图标层（SHELLDLL_DefView）的父窗口并不固定。Explorer 在“显示桌面”等
/// 场景会把 DefView 从 Progman 挪到某个 WorkerW 下，且**不会**带上我们的外部子窗口。
/// 因此覆盖层必须跟随 DefView 的宿主，否则两者分处不同顶层窗口时，图标会盖住覆盖层。
/// </summary>
public sealed class DesktopHost
{
    /// <summary>Progman（桌面根窗口），可能为 0。</summary>
    public IntPtr Progman { get; private set; }

    /// <summary>SHELLDLL_DefView（桌面图标层），可能为 0（例如关闭了桌面图标）。</summary>
    public IntPtr DefView { get; private set; }

    /// <summary>当前承载图标层的宿主窗口：Progman 或某个 WorkerW。</summary>
    public IntPtr IconLayerHost { get; private set; }

    /// <summary>宿主客户区宽度（像素）。</summary>
    public int Width { get; private set; }

    /// <summary>宿主客户区高度（像素）。</summary>
    public int Height { get; private set; }

    public bool IsValid => IconLayerHost != IntPtr.Zero && NativeMethods.IsWindow(IconLayerHost);

    /// <summary>定位桌面窗口与图标层宿主，并刷新尺寸。返回是否成功。</summary>
    public bool TryLocate()
    {
        Progman = NativeMethods.FindWindow("Progman", IntPtr.Zero);

        DefView = IntPtr.Zero;
        IconLayerHost = IntPtr.Zero;

        if (Progman != IntPtr.Zero)
        {
            var defView = NativeMethods.FindWindowEx(Progman, IntPtr.Zero, "SHELLDLL_DefView", null);
            if (defView != IntPtr.Zero)
            {
                DefView = defView;
                IconLayerHost = Progman;
            }
        }

        if (IconLayerHost == IntPtr.Zero)
        {
            var worker = IntPtr.Zero;
            while ((worker = NativeMethods.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
            {
                var defView = NativeMethods.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                {
                    DefView = defView;
                    IconLayerHost = worker;
                    break;
                }
            }
        }

        // 没有图标层（或图标被关闭）时退回 Progman
        if (IconLayerHost == IntPtr.Zero)
        {
            IconLayerHost = Progman;
        }

        if (IconLayerHost == IntPtr.Zero)
        {
            Width = 0;
            Height = 0;
            return false;
        }

        NativeMethods.GetClientRect(IconLayerHost, out var rect);
        Width = Math.Max(1, rect.Width);
        Height = Math.Max(1, rect.Height);
        return true;
    }

    /// <summary>
    /// 让覆盖层挂到当前图标层宿主下，并排在宿主子窗口 z 序最上方。
    /// 宿主切换（Progman ↔ WorkerW）时会自动 SetParent 跟随，只在确认是桌面层宿主时才动。
    /// </summary>
    /// <summary>隐藏/恢复桌面图标列表（透明桌面模式用，让动态壁纸完整显示）。</summary>
    public bool SetDesktopIconsVisible(bool visible)
    {
        if (!TryLocate())
        {
            return false;
        }

        var listView = NativeMethods.FindWindowEx(DefView, IntPtr.Zero, "SysListView32", null);
        if (listView == IntPtr.Zero)
        {
            AppLog.Warn("未找到桌面图标列表，无法切换图标显示");
            return false;
        }

        var ok = NativeMethods.ShowWindow(listView, visible ? NativeMethods.SW_SHOW : NativeMethods.SW_HIDE);
        AppLog.Info(visible ? "桌面图标已恢复" : "桌面图标已隐藏（透明桌面模式）");
        return ok;
    }

    public bool EnsureChildAttached(IntPtr child)
    {
        if (child == IntPtr.Zero || !NativeMethods.IsWindow(child) || IconLayerHost == IntPtr.Zero)
        {
            return false;
        }

        if (!IsDesktopLayer(IconLayerHost))
        {
            return false;
        }

        var parent = NativeMethods.GetAncestor(child, NativeMethods.GA_PARENT);
        if (parent != IconLayerHost)
        {
            NativeMethods.SetParent(child, IconLayerHost);
            AppLog.Info($"覆盖层跟随图标层切换到宿主 {NativeMethods.ClassNameOf(IconLayerHost)}");
        }

        var top = NativeMethods.GetWindow(IconLayerHost, NativeMethods.GW_CHILD);
        var visible = NativeMethods.IsWindowVisible(child);
        if (top == child && visible)
        {
            return true;
        }

        NativeMethods.SetWindowPos(
            child,
            NativeMethods.HWND_TOP,
            0,
            0,
            0,
            0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);

        top = NativeMethods.GetWindow(IconLayerHost, NativeMethods.GW_CHILD);
        if (top == child)
        {
            AppLog.Info("z 序已修复：覆盖层重新位于桌面图标层之上");
            return true;
        }

        AppLog.Warn($"z 序修复失败，宿主子窗口最上层为 0x{top.ToInt64():X8}");
        return false;
    }

    /// <summary>桌面层宿主：Progman 或承载桌面子窗口的 WorkerW。</summary>
    private static bool IsDesktopLayer(IntPtr window)
    {
        var className = NativeMethods.ClassNameOf(window);
        return className.Equals("WorkerW", StringComparison.Ordinal)
               || className.Equals("Progman", StringComparison.Ordinal);
    }
}
