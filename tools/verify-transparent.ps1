[CmdletBinding()]
param()

# 透明桌面模式（Wallpaper Engine 动态壁纸）验收
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'dist\PhoneDesktop\PhoneDesktop.exe'
$shotDir = Join-Path $root 'artifacts\shots\transparent'
$reportPath = Join-Path $root 'artifacts\verify-transparent-report.txt'
$uiPath = Join-Path $env:APPDATA 'PhoneDesktop\dev-ui.json'
$logPath = Join-Path $env:APPDATA 'PhoneDesktop\log.txt'
$settingsPath = Join-Path $env:APPDATA 'PhoneDesktop\settings.json'

if (-not (Test-Path $exe)) { throw '请先运行 build.ps1 -Action publish' }
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PDT {
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowW")] public static extern IntPtr FindWindow(string c, IntPtr w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowExW")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint a, uint p, ref RECT v, uint w);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static string Cls(IntPtr h) { var sb = new StringBuilder(256); GetClassName(h, sb, 256); return sb.ToString(); }
  public static bool HitOurs(int x, int y) {
    var h = WindowFromPoint(new POINT { X = x, Y = y }); var g = 0;
    while (h != IntPtr.Zero && g++ < 12) { if (Cls(h).Contains("PhoneDesktop")) return true; h = GetParent(h); }
    return false;
  }
  public static IntPtr Icons() {
    var pm = FindWindow("Progman", IntPtr.Zero);
    var dv = FindWindowEx(pm, IntPtr.Zero, "SHELLDLL_DefView", null);
    if (dv == IntPtr.Zero) { var w = IntPtr.Zero; while ((w = FindWindowEx(IntPtr.Zero, w, "WorkerW", null)) != IntPtr.Zero) { var d2 = FindWindowEx(w, IntPtr.Zero, "SHELLDLL_DefView", null); if (d2 != IntPtr.Zero) { dv = d2; break; } } }
    return dv == IntPtr.Zero ? IntPtr.Zero : FindWindowEx(dv, IntPtr.Zero, "SysListView32", null);
  }
  public static string WorkArea() { var r = new RECT(); SystemParametersInfo(0x0030, 0, ref r, 0); return string.Format("{0}x{1}", r.R - r.L, r.B - r.T); }
  public static bool Taskbar() { var h = FindWindow("Shell_TrayWnd", IntPtr.Zero); return h != IntPtr.Zero && IsWindowVisible(h); }
}
'@

$results = New-Object System.Collections.Generic.List[string]
function Pass([string]$m) { $script:results.Add("PASS  $m") | Out-Null; Write-Host "  PASS  $m" -ForegroundColor Green }
function Fail([string]$m) { $script:results.Add("FAIL  $m") | Out-Null; Write-Host "  FAIL  $m" -ForegroundColor Red }
function Section([string]$m) { $script:results.Add("") | Out-Null; $script:results.Add("## $m") | Out-Null; Write-Host ""; Write-Host "== $m ==" -ForegroundColor Cyan }

function Stop-App {
    if (Get-Process PhoneDesktop -ErrorAction SilentlyContinue) {
        $p = Start-Process -FilePath $exe -ArgumentList '--exit' -PassThru
        $p.WaitForExit(5000) | Out-Null
        Start-Sleep -Milliseconds 1500
        if (Get-Process PhoneDesktop -ErrorAction SilentlyContinue) { Get-Process PhoneDesktop | Stop-Process -Force; Start-Sleep -Milliseconds 600 }
    }
}

# 备份配置并打开开关
$backup = Join-Path $env:TEMP 'PhoneDesktop-transparent-backup.json'
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backup -Force }
Stop-App
Start-Sleep -Milliseconds 500
if (Test-Path $settingsPath) {
    $json = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $json | Add-Member -NotePropertyName UseTransparentDesktop -NotePropertyValue $true -Force
    $json | ConvertTo-Json -Depth 10 | Out-File $settingsPath -Encoding UTF8
} else {
    "{ `"UseTransparentDesktop`": true }" | Out-File $settingsPath -Encoding UTF8
}
Remove-Item $uiPath -ErrorAction SilentlyContinue

Section '透明桌面模式'
Start-Process -FilePath $exe -ArgumentList '--dev' | Out-Null
Start-Sleep -Seconds 3
(New-Object -ComObject Shell.Application).ToggleDesktop()
Start-Sleep -Milliseconds 1500

$ui = $null
for ($i = 0; $i -lt 25; $i++) {
    try { $ui = Get-Content $uiPath -Raw -Encoding UTF8 | ConvertFrom-Json; if ($ui.dock) { break } } catch { }
    Start-Sleep -Milliseconds 200
}
if (-not $ui) { throw '未获取到 UI 探针' }

$dockX = [int]($ui.dock.X + $ui.dock.Width / 2)
$dockY = [int]($ui.dock.Y + $ui.dock.Height / 2)
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

if (-not [PDT]::IsWindowVisible([PDT]::Icons())) {
    Pass '桌面图标已隐藏'
} else {
    Fail '桌面图标仍然可见'
}

# 守护每 1 秒把 Dock 层提回桌面之上，这里最多等 3 秒
$hit = $false
for ($i = 0; $i -lt 15 -and -not $hit; $i++) {
    # Dock 宽度会随运行项变化，每次都用最新探针重算中心点
    try {
        $fresh = Get-Content $uiPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($fresh.dock) {
            $dockX = [int]($fresh.dock.X + $fresh.dock.Width / 2)
            $dockY = [int]($fresh.dock.Y + $fresh.dock.Height / 2)
        }
    } catch { }
    $hit = [PDT]::HitOurs($dockX, $dockY)
    if (-not $hit) { Start-Sleep -Milliseconds 250 }
}
if ($hit) {
    Pass 'Dock 在桌面之上且可点击'
} else {
    Fail 'Dock 未被命中（透明层不可点）'
}

if (-not [PDT]::HitOurs(640, 300)) {
    Pass '桌面空白区域点击穿透（动态壁纸可见）'
} else {
    Fail '桌面空白区域仍被覆盖层挡住'
}

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$g = [System.Drawing.Graphics]::FromImage($bitmap)
$g.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
$bitmap.Save((Join-Path $shotDir '70-transparent-desktop.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bitmap.Dispose()
Pass "70 透明桌面截图：$(Join-Path $shotDir '70-transparent-desktop.png')"

Section '显示桌面（Win+D）后 Dock 仍在'
(New-Object -ComObject Shell.Application).ToggleDesktop()
Start-Sleep -Milliseconds 2500
if ([PDT]::HitOurs($dockX, $dockY)) {
    Pass 'Win+D 之后 Dock 仍然可见可点'
} else {
    Fail 'Win+D 之后 Dock 消失'
}

Section '退出恢复'
Stop-App
Start-Sleep -Milliseconds 1200
if ([PDT]::IsWindowVisible([PDT]::Icons())) {
    Pass '退出后桌面图标恢复'
} else {
    Fail '退出后桌面图标仍隐藏'
}
$workArea = [PDT]::WorkArea()
if ($workArea -ne "$($screen.Width)x$($screen.Height)") {
    Pass "退出后工作区恢复：$workArea"
} else {
    Fail "退出后工作区仍是整屏：$workArea"
}
if ([PDT]::Taskbar()) { Pass '退出后任务栏恢复' } else { Fail '退出后任务栏未恢复' }

if (Test-Path $backup) {
    Copy-Item $backup $settingsPath -Force
    Remove-Item $backup -ErrorAction SilentlyContinue
}

$failCount = ($results | Where-Object { $_ -like 'FAIL*' }).Count
$summary = @('# 透明桌面模式验收报告', '', "时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')", "日志：$logPath", '') + $results + @('', $(if ($failCount -eq 0) { '结论：全部通过' } else { "结论：$failCount 项失败" }))
$summary -join [Environment]::NewLine | Out-File $reportPath -Encoding UTF8
Write-Host ""
Write-Host "报告：$reportPath" -ForegroundColor Cyan
if ($failCount -gt 0) { exit 1 }
