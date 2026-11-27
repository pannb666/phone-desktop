namespace PhoneDesktop.Models;

/// <summary>抽屉里的一个条目（桌面快捷方式/文件，或开始菜单应用）。</summary>
public sealed class CatalogItem
{
    public required string DisplayName { get; set; }

    /// <summary>.lnk / .url / 文件 / 文件夹 的真实路径（启动时用它）。</summary>
    public required string Path { get; init; }

    /// <summary>.lnk 解析出的目标路径（用于搜索与去重）。</summary>
    public string TargetPath { get; init; } = string.Empty;

    public string Arguments { get; init; } = string.Empty;

    /// <summary>desktop / startmenu</summary>
    public required string Source { get; init; }

    /// <summary>shortcut / url / file / folder</summary>
    public required string Kind { get; init; }

    /// <summary>预计算的小写检索串：名称 + 文件名 + 路径 + 目标路径 + 参数。</summary>
    public required string SearchBlob { get; set; }

    public bool Matches(string lowerQuery)
        => string.IsNullOrWhiteSpace(lowerQuery) || SearchBlob.Contains(lowerQuery, StringComparison.Ordinal);
}
