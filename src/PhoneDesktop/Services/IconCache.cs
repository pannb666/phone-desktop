using System.IO;
using System.Windows.Media.Imaging;
using PhoneDesktop.Interop;

namespace PhoneDesktop.Services;

/// <summary>
/// 图标缓存：按路径 + 修改时间做键，冻结位图后可跨线程使用。
/// 上限 600 条，超出后按插入顺序淘汰（阶段 3 抽屉会有几百个条目）。
/// </summary>
public sealed class IconCache
{
    private const int MaxEntries = 600;

    private readonly object _sync = new();
    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<string> _insertionOrder = new();

    public BitmapSource? Get(string path, int size = 96)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var key = BuildKey(path, size);
        lock (_sync)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                return cached;
            }
        }

        var icon = ShellIcon.Load(path, size);
        lock (_sync)
        {
            if (_cache.Count >= MaxEntries && _insertionOrder.Count > 0)
            {
                _cache.Remove(_insertionOrder.Dequeue());
            }

            _cache[key] = icon;
            _insertionOrder.Enqueue(key);
        }

        return icon;
    }

    /// <summary>只取已缓存的图标（不触发提取），用于预热完成后一次性填充。</summary>
    public bool TryGetCached(string path, int size, out BitmapSource? icon)
    {
        icon = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        var key = BuildKey(path, size);
        lock (_sync)
        {
            return _cache.TryGetValue(key, out icon);
        }
    }

    private static string BuildKey(string path, int size)
    {
        long stamp = 0;
        try
        {
            stamp = File.GetLastWriteTimeUtc(path).Ticks;
        }
        catch
        {
            // 忽略
        }

        return $"{path}|{stamp}|{size}";
    }
}
