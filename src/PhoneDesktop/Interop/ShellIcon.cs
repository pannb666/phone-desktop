using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using PhoneDesktop.Core;

namespace PhoneDesktop.Interop;

/// <summary>
/// 提取文件/快捷方式的大图标。
/// 主路径 IShellItemImageFactory（能拿到 .lnk 的目标图标、Win10 的现代图标），
/// 失败时退到 SHGetFileInfo。
/// </summary>
public static class ShellIcon
{
    private const uint SiigbfBiggerSizeOk = 0x00000001;
    private const uint SiigbfIconOnly = 0x00000004;

    private const uint ShgfiIcon = 0x00000100;
    private const uint ShgfiLargeIcon = 0x00000000;

    public static BitmapSource? Load(string path, int size = 96)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            var fromShellItem = LoadViaShellItem(path, size);
            if (fromShellItem is not null)
            {
                return fromShellItem;
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"IShellItemImageFactory 提取图标失败 {path}：{ex.Message}");
        }

        try
        {
            return LoadViaShellFileInfo(path);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"SHGetFileInfo 提取图标失败 {path}：{ex.Message}");
            return null;
        }
    }

    private static BitmapSource? LoadViaShellItem(string path, int size)
    {
        object? item = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out item);
            if (item is not IShellItemImageFactory factory)
            {
                return null;
            }

            var requested = new SIZE { cx = size, cy = size };
            var hr = factory.GetImage(requested, SiigbfIconOnly | SiigbfBiggerSizeOk, out var hBitmap);
            if (hr != 0 || hBitmap == IntPtr.Zero)
            {
                return null;
            }

            try
            {
                var bitmap = Imaging.CreateBitmapSourceFromHBitmap(
                    hBitmap,
                    IntPtr.Zero,
                    Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                bitmap.Freeze();
                return bitmap;
            }
            finally
            {
                DeleteObject(hBitmap);
            }
        }
        finally
        {
            if (item is not null)
            {
                Marshal.ReleaseComObject(item);
            }
        }
    }

    private static BitmapSource? LoadViaShellFileInfo(string path)
    {
        var info = new SHFILEINFO();
        var result = SHGetFileInfo(path, 0, ref info, (uint)Marshal.SizeOf<SHFILEINFO>(), ShgfiIcon | ShgfiLargeIcon);
        if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var bitmap = Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon,
                Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            bitmap.Freeze();
            return bitmap;
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    private static extern void SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindingContext,
        [In] ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object shellItem);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr handle);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(
        string path,
        uint fileAttributes,
        ref SHFILEINFO fileInfo,
        uint fileInfoSize,
        uint flags);

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, uint flags, out IntPtr bitmapHandle);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szDisplayName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string szTypeName;
    }
}
