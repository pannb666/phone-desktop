using System.IO;
using System.Windows.Media;
using PhoneDesktop.Core;
using PhoneDesktop.Services;

namespace PhoneDesktop.Overlay.Dock;

/// <summary>Dock 里“运行中的应用”一项，对应一个 exe（多个窗口取代表窗口）。</summary>
public sealed class RunningAppViewModel : ObservableObject
{
    private RunningApp _app;

    public RunningAppViewModel(RunningApp app, IconCache icons)
    {
        _app = app;
        Icon = ResolveIcon(app, icons);
    }

    public RunningApp App => _app;

    public string Key => _app.Key;

    public string Title => _app.Title;

    public bool IsActive => _app.IsActive;

    public bool IsMinimized => _app.IsMinimized;

    public ImageSource? Icon { get; }

    public string Initial
    {
        get
        {
            var name = Path.GetFileNameWithoutExtension(_app.ExecutablePath);
            if (string.IsNullOrWhiteSpace(name))
            {
                name = _app.Title;
            }

            return string.IsNullOrWhiteSpace(name) ? "?" : name[..1].ToUpperInvariant();
        }
    }

    public string Tooltip => _app.IsMinimized
        ? $"{Title}\n已最小化 · 点击恢复"
        : _app.IsActive
            ? $"{Title}\n当前窗口 · 点击最小化"
            : $"{Title}\n点击切换";

    public void Update(RunningApp app)
    {
        _app = app;
        OnPropertyChanged(nameof(App));
        OnPropertyChanged(nameof(Title));
        OnPropertyChanged(nameof(IsActive));
        OnPropertyChanged(nameof(IsMinimized));
        OnPropertyChanged(nameof(Tooltip));
    }

    private static ImageSource? ResolveIcon(RunningApp app, IconCache icons)
    {
        if (string.IsNullOrEmpty(app.ExecutablePath) || !File.Exists(app.ExecutablePath))
        {
            return null;
        }

        try
        {
            return icons.Get(app.ExecutablePath);
        }
        catch
        {
            return null;
        }
    }
}
