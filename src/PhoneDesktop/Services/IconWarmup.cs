using System.Windows;
using System.Windows.Media.Imaging;
using PhoneDesktop.Core;
using PhoneDesktop.Models;

namespace PhoneDesktop.Services;

/// <summary>
/// 后台 STA 线程预热图标缓存。
/// 抽屉里有 100+ 条目，逐个在 UI 线程提取会与首次布局抢时间；
/// 改成后台线程只写缓存（冻结位图可跨线程），UI 线程随后直接取。
/// </summary>
public static class IconWarmup
{
    private static int _running;

    public static void Start(IReadOnlyList<CatalogItem> items, IconCache cache, int size, Action? onCompleted = null)
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return;
        }

        var thread = new Thread(() =>
        {
            try
            {
                foreach (var item in items)
                {
                    if (string.IsNullOrWhiteSpace(item.Path))
                    {
                        continue;
                    }

                    cache.Get(item.Path, size);
                }
            }
            catch (Exception ex)
            {
                AppLog.Warn($"图标预热失败：{ex.Message}");
            }
            finally
            {
                Interlocked.Exchange(ref _running, 0);
            }

            if (onCompleted is not null)
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher is not null)
                {
                    dispatcher.BeginInvoke(onCompleted);
                }
            }
        })
        {
            IsBackground = true,
            Name = "PhoneDesktop.IconWarmup",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
    }
}
