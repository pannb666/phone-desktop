using System.Diagnostics;
using System.IO;
using PhoneDesktop.Core;

namespace PhoneDesktop.Interop;

/// <summary>统一的应用/文件启动入口（ShellExecute 语义，能处理 .lnk / .url / exe）。</summary>
public static class ShellLauncher
{
    public static bool TryLaunch(string pathOrCommand, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(pathOrCommand))
        {
            error = "命令为空";
            return false;
        }

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = pathOrCommand,
                UseShellExecute = true,
            };

            try
            {
                var directory = Path.GetDirectoryName(pathOrCommand);
                if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
                {
                    startInfo.WorkingDirectory = directory;
                }
            }
            catch
            {
                // 目录推导失败不影响启动
            }

            Process.Start(startInfo);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            AppLog.Warn($"启动失败 {pathOrCommand}：{ex.Message}");
            return false;
        }
    }
}
