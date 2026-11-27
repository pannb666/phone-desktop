using System.Collections.ObjectModel;
using System.IO;
using PhoneDesktop.Core;
using PhoneDesktop.Models;
using PhoneDesktop.Overlay.Dock;

namespace PhoneDesktop.Services;

/// <summary>
/// Dock 固定列表的加载/保存。首次运行写入一组常用应用作为初始内容，
/// 之后的顺序与增删全部以 settings.json 为准（阶段 4 提供固定/取消固定/拖拽）。
/// </summary>
public sealed class DockService
{
    private readonly SettingsService _settingsService;
    private readonly IconCache _icons;
    private readonly AppSettings _settings;

    public DockService(SettingsService settingsService, IconCache icons)
    {
        _settingsService = settingsService;
        _icons = icons;
        _settings = settingsService.Settings;
    }

    public ObservableCollection<DockItemViewModel> Items { get; } = new();

    public void Load()
    {
        Items.Clear();

        if (!_settings.DockInitialized && _settings.Dock.Count == 0)
        {
            foreach (var (name, path) in DefaultEntries())
            {
                if (!File.Exists(path))
                {
                    continue;
                }

                Items.Add(CreateItem(new DockEntry { Name = name, Path = path, Kind = "app" }));
            }

            _settings.DockInitialized = true;
            Save();
            AppLog.Info($"首次运行：写入 {Items.Count} 个默认 Dock 项");
        }
        else
        {
            foreach (var entry in _settings.Dock)
            {
                Items.Add(CreateItem(entry));
            }

            AppLog.Info($"Dock 载入 {Items.Count} 项");
        }
    }

    public bool IsPinned(string path)
        => Items.Any(item => string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>固定到 Dock（已存在则返回 null）。</summary>
    public DockItemViewModel? Add(CatalogItem item)
    {
        if (IsPinned(item.Path))
        {
            return null;
        }

        var viewModel = CreateItem(new DockEntry
        {
            Path = item.Path,
            Name = item.DisplayName,
            Kind = item.Kind,
        });

        Items.Add(viewModel);
        Save();
        AppLog.Info($"已固定到 Dock：{item.DisplayName}");
        return viewModel;
    }

    public bool Remove(DockItemViewModel item)
    {
        if (!Items.Remove(item))
        {
            return false;
        }

        Save();
        AppLog.Info($"已从 Dock 取消固定：{item.Name}");
        return true;
    }

    /// <summary>拖拽排序（只改内存顺序，落库由调用方在拖拽结束时统一 Save）。</summary>
    public void Move(int from, int to)
    {
        if (from < 0 || from >= Items.Count || to < 0 || to >= Items.Count || from == to)
        {
            return;
        }

        Items.Move(from, to);
    }

    public void Save()
    {
        _settings.Dock = Items.Select(item => item.ToEntry()).ToList();
        _settings.DockInitialized = true;
        _settingsService.Save();
    }

    private DockItemViewModel CreateItem(DockEntry entry)
    {
        var item = new DockItemViewModel(entry);
        item.SetIcon(_icons.Get(entry.Path));
        return item;
    }

    private static (string Name, string Path)[] DefaultEntries()
    {
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var system = Environment.SystemDirectory;

        return new[]
        {
            ("文件资源管理器", Path.Combine(windows, "explorer.exe")),
            ("记事本", Path.Combine(system, "notepad.exe")),
            ("画图", Path.Combine(system, "mspaint.exe")),
            ("计算器", Path.Combine(system, "calc.exe")),
            ("命令提示符", Path.Combine(system, "cmd.exe")),
        };
    }
}
