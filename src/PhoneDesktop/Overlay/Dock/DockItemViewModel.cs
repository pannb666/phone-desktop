using System.IO;
using System.Windows.Media.Imaging;
using PhoneDesktop.Core;
using PhoneDesktop.Models;

namespace PhoneDesktop.Overlay.Dock;

public sealed class DockItemViewModel : ObservableObject
{
    private BitmapSource? _icon;

    public DockItemViewModel(DockEntry entry)
    {
        Path = entry.Path;
        Name = entry.Name;
        Kind = entry.Kind;
    }

    public string Path { get; }

    public string Name { get; }

    public string Kind { get; }

    /// <summary>图标加载完成前的占位文字。</summary>
    public string Initial => string.IsNullOrWhiteSpace(Name) ? "?" : Name[..1];

    public bool FileExists => File.Exists(Path);

    public BitmapSource? Icon
    {
        get => _icon;
        private set => SetProperty(ref _icon, value);
    }

    public void SetIcon(BitmapSource? icon) => Icon = icon;

    public DockEntry ToEntry() => new()
    {
        Path = Path,
        Name = Name,
        Kind = Kind,
    };
}
