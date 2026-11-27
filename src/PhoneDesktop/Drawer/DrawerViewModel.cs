using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using PhoneDesktop.Core;
using PhoneDesktop.Models;
using PhoneDesktop.Services;

namespace PhoneDesktop.Drawer;

public sealed class DrawerViewModel : ObservableObject
{
    public const int IconSize = 80;

    private const int IconBatchSize = 16;

    private readonly AppCatalog _catalog;
    private readonly IconCache _icons;
    private readonly Dictionary<CatalogItem, DrawerItemViewModel> _cache = new();
    private readonly Queue<DrawerItemViewModel> _iconQueue = new();
    private readonly DispatcherTimer _iconTimer;

    private string _search = string.Empty;
    private bool _isLoading;

    public DrawerViewModel(AppCatalog catalog, IconCache icons)
    {
        _catalog = catalog;
        _icons = icons;
        _isLoading = !catalog.IsReady;

        _iconTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(25),
        };
        _iconTimer.Tick += OnIconTick;

        _catalog.Ready += OnCatalogReady;
        Refresh();
    }

    public ObservableCollection<DrawerItemViewModel> Items { get; } = new();

    public bool IsLoading
    {
        get => _isLoading;
        private set => SetProperty(ref _isLoading, value);
    }

    public int TotalCount => _catalog.Items.Count;

    public bool IsSorted => _catalog.IsSorted;

    public int FilteredCount => Items.Count;

    public bool IsEmpty => !IsLoading && Items.Count == 0;

    public string CountText => IsLoading ? "正在加载应用…" : $"{Items.Count} / {TotalCount}";

    /// <summary>搜索串（已小写）。</summary>
    public string Query => _search;

    public void SetSearch(string? text)
    {
        _search = (text ?? string.Empty).Trim().ToLowerInvariant();
        Refresh();
    }

    public void Refresh()
    {
        Items.Clear();

        if (!IsLoading)
        {
            foreach (var item in _catalog.Items)
            {
                if (item.Matches(_search))
                {
                    Items.Add(GetOrCreate(item));
                }
            }
        }

        OnPropertyChanged(nameof(FilteredCount));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        OnPropertyChanged(nameof(TotalCount));

        QueueIcons();
    }

    public void PauseIconLoading() => _iconTimer.Stop();

    /// <summary>窗口关闭时解除对目录事件的订阅。</summary>
    public void Detach()
    {
        _iconTimer.Stop();
        _catalog.Ready -= OnCatalogReady;
    }

    /// <summary>关闭抽屉时释放瓦片与视图模型引用，避免整棵可视树被窗口对象长期持有。</summary>
    public void Release()
    {
        Detach();
        _iconQueue.Clear();
        _cache.Clear();
        Items.Clear();
    }

    /// <summary>把已缓存的图标一次性填到当前列表（预热完成 / 打开抽屉时用）。</summary>
    public void ApplyCachedIcons()
    {
        foreach (var viewModel in Items)
        {
            if (viewModel.Icon is not null)
            {
                continue;
            }

            if (_icons.TryGetCached(viewModel.Path, IconSize, out var icon) && icon is not null)
            {
                viewModel.SetIcon(icon);
            }
        }
    }

    private void OnCatalogReady(object? sender, EventArgs e)
    {
        Application.Current?.Dispatcher.BeginInvoke(new Action(() =>
        {
            IsLoading = false;
            Refresh();
        }));
    }

    private DrawerItemViewModel GetOrCreate(CatalogItem item)
    {
        if (_cache.TryGetValue(item, out var cached))
        {
            return cached;
        }

        var viewModel = new DrawerItemViewModel(item);
        _cache[item] = viewModel;
        return viewModel;
    }

    private void QueueIcons()
    {
        _iconQueue.Clear();
        foreach (var viewModel in Items)
        {
            if (viewModel.Icon is null)
            {
                _iconQueue.Enqueue(viewModel);
            }
        }

        if (_iconQueue.Count > 0)
        {
            _iconTimer.Start();
        }
        else
        {
            _iconTimer.Stop();
        }
    }

    private void OnIconTick(object? sender, EventArgs e)
    {
        for (var index = 0; index < IconBatchSize && _iconQueue.Count > 0; index++)
        {
            var viewModel = _iconQueue.Dequeue();
            if (viewModel.Icon is null)
            {
                viewModel.SetIcon(_icons.Get(viewModel.Path, IconSize));
            }
        }

        if (_iconQueue.Count == 0)
        {
            _iconTimer.Stop();
        }
    }
}
