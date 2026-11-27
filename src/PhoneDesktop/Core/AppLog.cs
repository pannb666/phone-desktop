using System.IO;
using System.Text;

namespace PhoneDesktop.Core;

/// <summary>
/// 极简日志：直接追加写入 %APPDATA%\PhoneDesktop\log.txt。
/// 层级问题几乎都靠它定位，所以写入频率很低（启动 / 挂载 / 守护事件 / 异常）。
/// </summary>
public static class AppLog
{
    private const long MaxBytes = 256 * 1024;

    private static readonly object Sync = new();
    private static string? _path;
    private static bool _ready;

    public static string? LogPath => _path;

    public static void Init()
    {
        lock (Sync)
        {
            if (_ready)
            {
                return;
            }

            try
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "PhoneDesktop");
                Directory.CreateDirectory(dir);
                _path = Path.Combine(dir, "log.txt");
                RotateIfNeeded(_path);
                _ready = true;
            }
            catch
            {
                // 日志不可用时也不能影响主流程
                _path = null;
            }
        }

        Info("================ PhoneDesktop 启动 ================");
        Info($"exe={Environment.ProcessPath} os={Environment.OSVersion} clr={Environment.Version}");
    }

    public static void Info(string message) => Write("INFO ", message);

    public static void Warn(string message) => Write("WARN ", message);

    public static void Error(string message, Exception? ex = null)
        => Write("ERROR", ex is null ? message : $"{message} | {ex.GetType().Name}: {ex.Message}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} {level} [t{Environment.CurrentManagedThreadId}] {message}";
        lock (Sync)
        {
            if (!_ready || _path is null)
            {
                return;
            }

            try
            {
                File.AppendAllText(_path, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 忽略日志失败
            }
        }
    }

    private static void RotateIfNeeded(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (info.Exists && info.Length > MaxBytes)
            {
                var backup = path + ".old";
                File.Delete(backup);
                File.Move(path, backup);
            }
        }
        catch
        {
            // 忽略
        }
    }
}
