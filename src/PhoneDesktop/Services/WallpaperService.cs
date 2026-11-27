using System.IO;
using System.Text;
using Microsoft.Win32;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using PhoneDesktop.Core;
using PhoneDesktop.Interop;

namespace PhoneDesktop.Services;

public sealed record WallpaperResult(BitmapSource? Image, Color SolidColor, string Description);

/// <summary>
/// 读取当前桌面壁纸。刻意不使用 IDesktopWallpaper COM ——
/// 实测该 COM 在 Explorer 繁忙时会阻塞调用线程（整个 UI 冻结），
/// 而需求只要求“读取当前壁纸”，用下面这条无阻塞链路已足够：
///
///   1. SystemParametersInfo(SPI_GETDESKWALLPAPER)  静态壁纸的权威路径
///   2. HKCU\Control Panel\Desktop\Wallpaper        兼容旧路径/环境变量
///   3. HKCU\Control Panel\Colors\Background        纯色壁纸的情况
///   4. 设置页里的兜底颜色
/// </summary>
public sealed class WallpaperService
{
    public WallpaperResult Load(int pixelWidth, int pixelHeight, Color fallbackColor)
    {
        foreach (var candidate in EnumerateImageCandidates())
        {
            if (string.IsNullOrWhiteSpace(candidate))
            {
                continue;
            }

            var path = Environment.ExpandEnvironmentVariables(candidate.Trim().Trim('"'));
            if (!File.Exists(path))
            {
                AppLog.Warn($"壁纸文件不存在：{path}");
                continue;
            }

            var image = TryDecode(path, pixelWidth);
            if (image is not null)
            {
                AppLog.Info($"壁纸来源：{path}（{image.PixelWidth}x{image.PixelHeight}）");
                return new WallpaperResult(image, Colors.Black, path);
            }
        }

        var desktopColor = TryReadDesktopBackgroundColor();
        if (desktopColor is { } color)
        {
            AppLog.Info($"未找到图片壁纸，使用桌面背景色 #{color.R:X2}{color.G:X2}{color.B:X2}");
            return new WallpaperResult(null, color, "桌面背景色");
        }

        AppLog.Warn($"壁纸读取失败，使用兜底颜色 #{fallbackColor.R:X2}{fallbackColor.G:X2}{fallbackColor.B:X2}");
        return new WallpaperResult(null, fallbackColor, "兜底颜色");
    }

    private static IEnumerable<string?> EnumerateImageCandidates()
    {
        yield return ReadSystemWallpaperPath();
        yield return ReadRegistryWallpaper();
    }

    private static string? ReadSystemWallpaperPath()
    {
        try
        {
            var buffer = new StringBuilder(260);
            if (NativeMethods.SystemParametersInfo(
                    NativeMethods.SPI_GETDESKWALLPAPER,
                    (uint)buffer.Capacity,
                    buffer,
                    0)
                && buffer.Length > 0)
            {
                return buffer.ToString();
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"SPI_GETDESKWALLPAPER 失败：{ex.Message}");
        }

        return null;
    }

    private static string? ReadRegistryWallpaper()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
            return key?.GetValue("Wallpaper") as string;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取壁纸注册表失败：{ex.Message}");
            return null;
        }
    }

    private static Color? TryReadDesktopBackgroundColor()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Control Panel\Colors");
            if (key?.GetValue("Background") is not string raw)
            {
                return null;
            }

            var parts = raw.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 3
                && byte.TryParse(parts[0], out var r)
                && byte.TryParse(parts[1], out var g)
                && byte.TryParse(parts[2], out var b))
            {
                return Color.FromRgb(r, g, b);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取桌面背景色失败：{ex.Message}");
        }

        return null;
    }

    private static BitmapSource? TryDecode(string path, int targetWidth)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            // 注意：不要用 CreateOptions = IgnoreImageCache。
            // 流式来源没有 URI，WPF 会拿 null 作为图像缓存键并抛 “Parameter 'key'”。
            bitmap.CreateOptions = BitmapCreateOptions.None;
            if (targetWidth > 0)
            {
                bitmap.DecodePixelWidth = targetWidth;
            }

            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"壁纸解码失败 {path}：{ex.Message}");
            return null;
        }
    }
}
