using System.Windows.Media;

namespace PhoneDesktop.Models;

/// <summary>
/// 全部持久化数据的根对象。阶段 1 只读取 FallbackColor，
/// 其余字段先落定结构，避免后续阶段迁移。
/// </summary>
public sealed class AppSettings
{
    public const string DefaultFallbackColor = "#FF1B1B1F";

    public int Version { get; set; } = 1;

    /// <summary>壁纸读取失败时使用的兜底颜色（#AARRGGBB 或 #RRGGBB）。</summary>
    public string FallbackColor { get; set; } = DefaultFallbackColor;

    public bool RunAtStartup { get; set; } = true;

    /// <summary>透明桌面模式：隐藏桌面图标，让 Wallpaper Engine 等动态壁纸直接可见。</summary>
    public bool UseTransparentDesktop { get; set; }

    /// <summary>Dock 是否已经初始化过（首次运行写入默认项后置位，避免用户清空后被重新填充）。</summary>
    public bool DockInitialized { get; set; }

    public HotkeySettings Hotkey { get; set; } = new();

    /// <summary>Dock 固定项，顺序即显示顺序。阶段 2 起使用。</summary>
    public List<DockEntry> Dock { get; set; } = new();

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(FallbackColor))
        {
            FallbackColor = DefaultFallbackColor;
        }

        Hotkey ??= new HotkeySettings();
        Dock ??= new List<DockEntry>();
        Dock.RemoveAll(entry => entry is null || string.IsNullOrWhiteSpace(entry.Path));
    }

    public Color ResolveFallbackColor()
    {
        try
        {
            if (ColorConverter.ConvertFromString(FallbackColor) is Color color)
            {
                return color.A == 0 ? Color.FromRgb(color.R, color.G, color.B) : color;
            }
        }
        catch
        {
            // 落到默认值
        }

        return Color.FromRgb(0x1B, 0x1B, 0x1F);
    }
}

public sealed class HotkeySettings
{
    /// <summary>形如 "Control+Alt"。</summary>
    public string Modifiers { get; set; } = "Control";

    /// <summary>WPF Key 名称，形如 "Space"。</summary>
    public string Key { get; set; } = "Space";
}

public sealed class DockEntry
{
    /// <summary>.lnk / .url / exe / 文件 的完整路径。</summary>
    public string Path { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    /// <summary>shortcut / file / app</summary>
    public string Kind { get; set; } = "shortcut";
}
