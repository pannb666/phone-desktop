using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using PhoneDesktop.Core;

namespace PhoneDesktop.Overlay.Dock;

public sealed record ProbeRect(double X, double Y, double Width, double Height);

public sealed record ProbeItem(string Name, string Path, double X, double Y, double Width, double Height);

/// <summary>运行中的应用：Path 是 exe 键，State 是 active / minimized / normal。</summary>
public sealed record ProbeRunning(string Path, string Title, string State, double X, double Y, double Width, double Height);

/// <summary>
/// 仅在 --dev 下写出的 UI 位置快照，供自动化验收脚本精确点击（阶段 2/3/4 复用）。
///   dev-ui.json     : 覆盖层上的 Dock、悬浮按钮等（按 section 合并写入）
///   dev-drawer.json : 抽屉打开时的窗口/搜索框/网格项位置，关闭时删除
/// </summary>
public static class DevUiProbe
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly object Sync = new();
    private static readonly Dictionary<string, object?> Sections = new(StringComparer.Ordinal);

    public static string OverlayPath => Path.Combine(AppDataDirectory, "dev-ui.json");

    public static string DrawerPath => Path.Combine(AppDataDirectory, "dev-drawer.json");

    private static string AppDataDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "PhoneDesktop");

    public static void SetSections(Dictionary<string, object?> sections)
    {
        if (!App.IsDevMode)
        {
            return;
        }

        lock (Sync)
        {
            foreach (var pair in sections)
            {
                Sections[pair.Key] = pair.Value;
            }

            Sections["capturedAt"] = DateTime.Now.ToString("O");
            WriteJson(OverlayPath, Sections);
        }
    }

    public static void WriteDrawer(Dictionary<string, object?> payload)
    {
        if (!App.IsDevMode)
        {
            return;
        }

        payload["capturedAt"] = DateTime.Now.ToString("O");
        WriteJson(DrawerPath, payload);
    }

    public static void DeleteDrawer()
    {
        if (!App.IsDevMode)
        {
            return;
        }

        try
        {
            if (File.Exists(DrawerPath))
            {
                File.Delete(DrawerPath);
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"删除 dev-drawer.json 失败：{ex.Message}");
        }
    }

    public static ProbeRect? RectOf(FrameworkElement? element)
    {
        if (element is null || element.ActualWidth <= 0 || element.ActualHeight <= 0)
        {
            return null;
        }

        if (PresentationSource.FromVisual(element) is null)
        {
            return null;
        }

        var dpi = VisualTreeHelper.GetDpi(element);
        var origin = element.PointToScreen(new Point(0, 0));

        return new ProbeRect(
            origin.X,
            origin.Y,
            element.ActualWidth * dpi.DpiScaleX,
            element.ActualHeight * dpi.DpiScaleY);
    }

    private static void WriteJson(string path, object value)
    {
        try
        {
            Directory.CreateDirectory(AppDataDirectory);
            var temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"{Path.GetFileName(path)} 写入失败：{ex.Message}");
        }
    }
}
