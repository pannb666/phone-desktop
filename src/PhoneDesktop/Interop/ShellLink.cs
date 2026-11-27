using System.Runtime.InteropServices;
using System.Text;
using PhoneDesktop.Core;

namespace PhoneDesktop.Interop;

/// <summary>读取 .lnk 的目标路径与参数（用于搜索与去重，启动仍走 ShellExecute）。</summary>
public static class ShellLink
{
    public static bool TryResolve(string shortcutPath, out string targetPath, out string arguments)
    {
        targetPath = string.Empty;
        arguments = string.Empty;

        object? link = null;
        try
        {
            link = new ShellLinkCoClass();

            if (link is not IPersistFile persistFile)
            {
                return false;
            }

            persistFile.Load(shortcutPath, 0 /* STGM_READ */);

            if (link is not IShellLinkW shellLink)
            {
                return false;
            }

            var pathBuffer = new StringBuilder(1024);
            shellLink.GetPath(pathBuffer, pathBuffer.Capacity, IntPtr.Zero, 0);
            targetPath = pathBuffer.ToString();

            var argumentBuffer = new StringBuilder(1024);
            shellLink.GetArguments(argumentBuffer, argumentBuffer.Capacity);
            arguments = argumentBuffer.ToString();

            return !string.IsNullOrWhiteSpace(targetPath);
        }
        catch (Exception ex)
        {
            AppLog.Warn($"解析快捷方式失败 {shortcutPath}：{ex.Message}");
            return false;
        }
        finally
        {
            if (link is not null)
            {
                try
                {
                    Marshal.ReleaseComObject(link);
                }
                catch
                {
                    // 忽略
                }
            }
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLinkCoClass
    {
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder filePath, int maxPath, IntPtr findData, uint flags);

        void GetIDList(out IntPtr itemIdList);

        void SetIDList(IntPtr itemIdList);

        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int maxName);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder directory, int maxPath);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder arguments, int maxPath);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCommand);

        void SetShowCmd(int showCommand);

        void GetIconLocation([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder iconPath, int maxPath, out int iconIndex);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string iconPath, int iconIndex);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        void Resolve(IntPtr window, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string filePath);
    }

    [ComImport]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [Guid("0000010B-0000-0000-C000-000000000046")]
    private interface IPersistFile
    {
        void GetClassID(out Guid classId);

        [PreserveSig]
        int IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string fileName, uint mode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string fileName, [MarshalAs(UnmanagedType.Bool)] bool remember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string fileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string fileName);
    }
}
