using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using PhoneDesktop.Core;
using PhoneDesktop.Models;

namespace PhoneDesktop.Services;

/// <summary>
/// settings.json 的读写。写入采用“临时文件 + 覆盖替换”，避免断电/崩溃留下半截文件。
/// </summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public string DirectoryPath { get; }

    public string FilePath { get; }

    public AppSettings Settings { get; private set; }

    public SettingsService()
    {
        DirectoryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PhoneDesktop");
        FilePath = Path.Combine(DirectoryPath, "settings.json");
        Settings = Load();
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var json = File.ReadAllText(FilePath);
                var loaded = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
                if (loaded is not null)
                {
                    loaded.Normalize();
                    return loaded;
                }
            }
        }
        catch (Exception ex)
        {
            AppLog.Warn($"settings.json 读取失败，使用默认配置：{ex.Message}");
        }

        var defaults = new AppSettings();
        defaults.Normalize();
        return defaults;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(Settings, JsonOptions));
            File.Move(temp, FilePath, overwrite: true);
            AppLog.Info($"设置已保存：{FilePath}");
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置保存失败：{ex.Message}");
        }
    }
}
