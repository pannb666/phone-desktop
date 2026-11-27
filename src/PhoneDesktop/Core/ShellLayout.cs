using System.Windows;

namespace PhoneDesktop.Core;

/// <summary>
/// 覆盖层内部元素的纵向布局参数（受任务栏是否隐藏影响）。
///
/// 注意：系统工作区（SPI_GETWORKAREA）在任务栏被 ShowWindow(SW_HIDE) 隐藏后通常**不会变化**，
/// 所以这里不能用工作区推算，必须直接看我们自己是否隐藏了任务栏。
/// </summary>
public static class ShellLayout
{
    public const int DockHeight = 82;

    /// <summary>Dock 距屏幕底部的间距。</summary>
    public static int DockBottomMargin => TaskbarController.IsHidden ? 18 : TaskbarInset + 12;

    /// <summary>右下角悬浮按钮距屏幕底部的间距（在 Dock 上方留 18px）。</summary>
    public static int DrawerButtonBottomMargin => DockBottomMargin + DockHeight + 18;

    /// <summary>轻提示距屏幕底部的间距（在 Dock 上方留出更多余量）。</summary>
    public static int ToastBottomMargin => DockBottomMargin + DockHeight + 26;

    /// <summary>未隐藏任务栏时，任务栏高度（DIP）。</summary>
    private static int TaskbarInset
    {
        get
        {
            var screenHeight = SystemParameters.PrimaryScreenHeight;
            var workBottom = SystemParameters.WorkArea.Bottom;
            var inset = (int)Math.Max(0, screenHeight - workBottom);
            return inset is > 0 and < 200 ? inset : 0;
        }
    }
}
