using Microsoft.Win32;
using PhoneDesktop.Core;

namespace PhoneDesktop.Services;

/// <summary>
/// 开机自启：写 HKCU\Software\Microsoft\Windows\CurrentVersion\Run。
/// 只用当前用户注册表，不需要管理员权限；退出即删。
/// </summary>
public static class AutostartService
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "PhoneDesktop";

    public static bool IsEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            var value = key?.GetValue(ValueName) as string;
            if (string.IsNullOrWhiteSpace(value))
            {
                return false;
            }

            var exe = Environment.ProcessPath;
            return exe is not null && value.Trim('"').StartsWith(exe, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"读取开机自启状态失败：{ex.Message}");
            return false;
        }
    }

    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (key is null)
            {
                return false;
            }

            if (enabled)
            {
                var exe = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exe))
                {
                    AppLog.Warn("无法确定可执行文件路径，开机自启未设置");
                    return false;
                }

                key.SetValue(ValueName, $"\"{exe}\"", RegistryValueKind.String);
                AppLog.Info($"已开启开机自启：{exe}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                AppLog.Info("已关闭开机自启");
            }

            return true;
        }
        catch (Exception ex)
        {
            AppLog.Warn($"设置开机自启失败：{ex.Message}");
            return false;
        }
    }
}
