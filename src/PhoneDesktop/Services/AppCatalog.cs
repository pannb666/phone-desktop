using System.Diagnostics;
using System.IO;
using PhoneDesktop.Core;
using PhoneDesktop.Interop;
using PhoneDesktop.Models;

namespace PhoneDesktop.Services;

/// <summary>
/// 抽屉内容来源：原生桌面（用户 + 公共）与开始菜单 Programs 目录。
/// 只做目录枚举 + 快捷方式解析，不做全盘索引；扫描在后台线程完成，不阻塞启动。
/// </summary>
public sealed class AppCatalog
{
    private readonly object _sync = new();
    private List<CatalogItem> _items = new();
    private bool _ready;
    private int _started;

    public IReadOnlyList<CatalogItem> Items
    {
        get
        {
            lock (_sync)
            {
                return _items;
            }
        }
    }

    public bool IsReady
    {
        get
        {
            lock (_sync)
            {
                return _ready;
            }
        }
    }

    /// <summary>目录是否已按名称（当前区域性）正确排序（应用内自检结果）。</summary>
    public bool IsSorted { get; private set; }

    /// <summary>在后台线程触发。</summary>
    public event EventHandler? Ready;

    /// <summary>后台扫描（幂等：只有第一次调用真正启动扫描）。</summary>
    public void StartLoad()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0 || _ready)
        {
            return;
        }

        _ = Task.Run(LoadCore);
    }

    public void LoadNow() => LoadCore();

    private void LoadCore()
    {
        if (_ready)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        try
        {
            var items = new List<CatalogItem>();
            var desktopCount = ScanDesktop(items);
            var startMenuCount = ScanStartMenu(items);

            var deduped = Dedupe(items, out var removedCount);
            var renamedCount = DisambiguateDisplayNames(deduped);
            deduped.Sort(CompareByName);

            // 用同一比较器再校验一遍最终顺序（.NET 8 使用 ICU 排序，
            // 与 Windows PowerShell 5.1 的 NLS 在标点/字母次序上会不同，所以由应用自检并输出结论）。
            var sorted = true;
            for (var index = 1; index < deduped.Count; index++)
            {
                if (CompareByName(deduped[index - 1], deduped[index]) > 0)
                {
                    sorted = false;
                    break;
                }
            }

            IsSorted = sorted;

            lock (_sync)
            {
                _items = deduped;
                _ready = true;
            }

            AppLog.Info(
                $"应用目录就绪：{deduped.Count} 项（桌面 {desktopCount}、开始菜单 {startMenuCount}，"
                + $"去重 {removedCount} 项、同名消歧 {renamedCount} 项），"
                + $"排序自检 {(sorted ? "通过" : "未通过")}，耗时 {watch.ElapsedMilliseconds} ms");
            Ready?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            AppLog.Error("应用目录扫描失败", ex);
            lock (_sync)
            {
                _ready = true;
            }

            Ready?.Invoke(this, EventArgs.Empty);
        }
    }

    private static int ScanDesktop(List<CatalogItem> items)
    {
        var count = 0;
        var directories = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory),
        };

        foreach (var directory in directories)
        {
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                continue;
            }

            IEnumerable<string> entries;
            try
            {
                entries = Directory.EnumerateFileSystemEntries(directory);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"枚举桌面失败 {directory}：{ex.Message}");
                continue;
            }

            foreach (var path in entries)
            {
                try
                {
                    var fileName = Path.GetFileName(path);
                    if (fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var attributes = File.GetAttributes(path);
                    if ((attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0)
                    {
                        continue;
                    }

                    var isFolder = (attributes & FileAttributes.Directory) != 0;
                    var extension = Path.GetExtension(path);
                    var isShortcut = !isFolder && extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
                    var isUrl = !isFolder && extension.Equals(".url", StringComparison.OrdinalIgnoreCase);

                    var displayName = isFolder ? fileName : Path.GetFileNameWithoutExtension(fileName);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        displayName = fileName;
                    }

                    var targetPath = string.Empty;
                    var arguments = string.Empty;
                    if (isShortcut)
                    {
                        ShellLink.TryResolve(path, out targetPath, out arguments);
                    }
                    else if (isUrl)
                    {
                        // .url 不是 .lnk，IShellLink 解析不出目标；直接读 InternetShortcut 的 URL。
                        // 拿到 URL 后，同一游戏的桌面副本与开始菜单副本才能被正确去重/搜索。
                        targetPath = TryReadUrlTarget(path);
                    }

                    items.Add(new CatalogItem
                    {
                        DisplayName = displayName,
                        Path = path,
                        TargetPath = targetPath,
                        Arguments = arguments,
                        Source = "desktop",
                        Kind = isFolder ? "folder" : isShortcut ? "shortcut" : isUrl ? "url" : "file",
                        SearchBlob = BuildSearchBlob(displayName, fileName, path, targetPath, arguments),
                    });
                    count++;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"跳过桌面项 {path}：{ex.Message}");
                }
            }
        }

        return count;
    }

    private static int ScanStartMenu(List<CatalogItem> items)
    {
        var count = 0;
        var roots = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs"),
        };

        foreach (var root in roots)
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            IEnumerable<string> files;
            try
            {
                files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories);
            }
            catch (Exception ex)
            {
                AppLog.Warn($"枚举开始菜单失败 {root}：{ex.Message}");
                continue;
            }

            foreach (var path in files)
            {
                try
                {
                    var extension = Path.GetExtension(path);
                    var isShortcut = extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase);
                    var isUrl = extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
                    if (!isShortcut && !isUrl)
                    {
                        continue;
                    }

                    var fileName = Path.GetFileName(path);
                    var displayName = Path.GetFileNameWithoutExtension(fileName);
                    if (string.IsNullOrWhiteSpace(displayName))
                    {
                        continue;
                    }

                    var targetPath = string.Empty;
                    var arguments = string.Empty;
                    if (isShortcut)
                    {
                        ShellLink.TryResolve(path, out targetPath, out arguments);
                    }
                    else if (isUrl)
                    {
                        // .url 不是 .lnk，IShellLink 解析不出目标；直接读 InternetShortcut 的 URL。
                        // 拿到 URL 后，同一游戏的桌面副本与开始菜单副本才能被正确去重/搜索。
                        targetPath = TryReadUrlTarget(path);
                    }

                    items.Add(new CatalogItem
                    {
                        DisplayName = displayName,
                        Path = path,
                        TargetPath = targetPath,
                        Arguments = arguments,
                        Source = "startmenu",
                        Kind = isShortcut ? "shortcut" : "url",
                        SearchBlob = BuildSearchBlob(displayName, fileName, path, targetPath, arguments),
                    });
                    count++;
                }
                catch (Exception ex)
                {
                    AppLog.Warn($"跳过开始菜单项 {path}：{ex.Message}");
                }
            }
        }

        return count;
    }

    /// <summary>
    /// 去重分两关：
    ///   1) 精确键：目标路径（或 .url 的 URL）+ 参数，完全一致视为同一条目；
    ///   2) 身份键：显示名 + 目标文件名，用来合并“同一程序在桌面与开始菜单各有一份快捷方式”
    ///      （两份 .lnk 可能带不同参数或不同大小写路径，但仍是同一个程序）。
    /// 桌面来源优先保留，其次开始菜单。
    /// </summary>
    private static List<CatalogItem> Dedupe(List<CatalogItem> items, out int removedCount)
    {
        var seenExact = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenIdentity = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<CatalogItem>(items.Count);
        removedCount = 0;

        foreach (var item in items.OrderBy(i => i.Source == "desktop" ? 0 : 1))
        {
            var target = (item.TargetPath ?? string.Empty).Trim();
            var exactKey = target.Length > 0
                ? $"{target}|{item.Arguments.Trim()}"
                : $"{item.Path}|{item.DisplayName}";

            if (!seenExact.Add(exactKey))
            {
                removedCount++;
                continue;
            }

            var identityKey = BuildIdentityKey(item);
            if (identityKey is not null && !seenIdentity.Add(identityKey))
            {
                removedCount++;
                continue;
            }

            result.Add(item);
        }

        return result;
    }

    private static string? BuildIdentityKey(CatalogItem item)
    {
        var name = item.DisplayName.Trim().ToLowerInvariant();
        if (name.Length == 0)
        {
            return null;
        }

        var target = (item.TargetPath ?? string.Empty).Trim();
        if (target.Length == 0)
        {
            return null;
        }

        if (target.Contains("://", StringComparison.Ordinal))
        {
            return $"{name}|url|{target.ToLowerInvariant()}";
        }

        var targetFileName = Path.GetFileName(target);
        return string.IsNullOrEmpty(targetFileName)
            ? null
            : $"{name}|exe|{targetFileName.ToLowerInvariant()}";
    }

    /// <summary>读取 .url（InternetShortcut）中的 URL。</summary>
    private static string TryReadUrlTarget(string path)
    {
        try
        {
            foreach (var line in File.ReadLines(path))
            {
                var trimmed = line.Trim();
                if (trimmed.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                {
                    return trimmed[4..].Trim();
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取 .url 失败 {path}：{ex.Message}");
        }

        return string.Empty;
    }

    /// <summary>
    /// 同名但确实是不同文件（例如 Install Resolve 20.3 的 .dat 与 .exe）时，
    /// 用“名称.扩展名”或“名称 (所在文件夹)”区分，避免用户看到两个一模一样的名字。
    /// </summary>
    private static int DisambiguateDisplayNames(List<CatalogItem> items)
    {
        var renamed = 0;

        var groups = items
            .GroupBy(item => item.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .ToList();

        foreach (var group in groups)
        {
            var entries = group.ToList();
            var extensions = entries
                .Select(item => Path.GetExtension(item.Path))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            foreach (var item in entries)
            {
                var fileName = Path.GetFileName(item.Path);
                var parent = Path.GetFileName(Path.GetDirectoryName(item.Path) ?? string.Empty);
                item.DisplayName = extensions.Count > 1
                    ? fileName
                    : string.IsNullOrEmpty(parent) ? fileName : $"{item.DisplayName} ({parent})";

                item.SearchBlob = BuildSearchBlob(item.DisplayName, fileName, item.Path, item.TargetPath, item.Arguments);
                renamed++;
            }
        }

        return renamed;
    }

    private static int CompareByName(CatalogItem left, CatalogItem right)
    {
        var compare = StringComparer.CurrentCultureIgnoreCase.Compare(left.DisplayName, right.DisplayName);
        return compare != 0
            ? compare
            : StringComparer.OrdinalIgnoreCase.Compare(left.Path, right.Path);
    }

    private static string BuildSearchBlob(
        string displayName,
        string fileName,
        string path,
        string targetPath,
        string arguments)
        => $"{displayName} {fileName} {path} {targetPath} {arguments}".ToLowerInvariant();
}
