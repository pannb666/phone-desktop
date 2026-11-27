[CmdletBinding()]
param(
    [string]$Name = '手机桌面',
    [string]$Target,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

# 在原生 Windows 桌面上创建/覆盖一个启动 PhoneDesktop 的快捷方式。
# 默认优先使用发布产物 dist\PhoneDesktop\PhoneDesktop.exe，其次用构建输出。

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot

if (-not $Target) {
    $published = Join-Path $root 'dist\PhoneDesktop\PhoneDesktop.exe'
    $built = Join-Path $root "src\PhoneDesktop\bin\$Configuration\net8.0-windows\PhoneDesktop.exe"

    if (Test-Path $published) {
        $Target = $published
    } elseif (Test-Path $built) {
        $Target = $built
        Write-Warning "未找到发布产物，改用构建输出：$built"
    } else {
        throw "找不到 PhoneDesktop.exe，请先运行 .\build.ps1 或 .\build.ps1 -Action publish"
    }
}

if (-not (Test-Path $Target)) {
    throw "目标不存在：$Target"
}

$desktop = [Environment]::GetFolderPath([Environment+SpecialFolder]::Desktop)
$shortcutPath = Join-Path $desktop "$Name.lnk"

$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $Target
$shortcut.WorkingDirectory = Split-Path -Parent $Target
$shortcut.IconLocation = "$Target,0"
$shortcut.Description = 'PhoneDesktop 手机风格桌面'
$shortcut.WindowStyle = 1
$shortcut.Save()
[void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)

# 让资源管理器立刻刷新桌面图标
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class ShellRefresh {
  [DllImport("shell32.dll")] public static extern void SHChangeNotify(int eventId, uint flags, IntPtr item1, IntPtr item2);
  public static void RefreshDesktop() { SHChangeNotify(0x08000000, 0x0000, IntPtr.Zero, IntPtr.Zero); }
}
'@
[ShellRefresh]::RefreshDesktop()

Write-Host "已创建桌面启动器：$shortcutPath" -ForegroundColor Green
Write-Host "目标：$Target"
