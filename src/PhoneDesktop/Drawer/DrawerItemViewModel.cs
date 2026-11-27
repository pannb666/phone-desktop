using System.Windows.Media.Imaging;
using PhoneDesktop.Core;
using PhoneDesktop.Models;

namespace PhoneDesktop.Drawer;

public sealed class DrawerItemViewModel : ObservableObject
{
    private BitmapSource? _icon;

    public DrawerItemViewModel(CatalogItem model)
    {
        Model = model;
    }

    public CatalogItem Model { get; }

    public string Name => Model.DisplayName;

    public string Path => Model.Path;

    public string TargetPath => Model.TargetPath;

    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1];

    /// <summary>图标未加载时的类型占位符（Segoe MDL2 Assets）。</summary>
    public string KindGlyph => Model.Kind switch
    {
        "folder" => "\uE8B7",
        "file" => "\uE8A5",
        "url" => "\uE774",
        _ => "\uE737",
    };

    public BitmapSource? Icon
    {
        get => _icon;
        private set => SetProperty(ref _icon, value);
    }

    public void SetIcon(BitmapSource? icon) => Icon = icon;
}
