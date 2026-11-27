[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

# 阶段 5.1：Dock 任务栏语义（运行中的应用 / 高亮 / 最小化还原）+ 最大化不留空白条

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "dist\PhoneDesktop\PhoneDesktop.exe"
if (-not (Test-Path $exe)) {
    $exe = Join-Path $root "src\PhoneDesktop\bin\$Configuration\net8.0-windows\PhoneDesktop.exe"
}
$shotDir = Join-Path $root 'artifacts\shots\running-dock'
$reportPath = Join-Path $root 'artifacts\verify-running-dock-report.txt'
$uiPath = Join-Path $env:APPDATA 'PhoneDesktop\dev-ui.json'

if (-not (Test-Path $exe)) { throw "未找到 PhoneDesktop.exe，请先 .\build.ps1 publish" }
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
public static class PDRD {
  [DllImport("user32.dll")] public static extern bool SystemParametersInfo(uint action, uint param, ref RECT value, uint winIni);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowW")] public static extern IntPtr FindWindow(string c, IntPtr w);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  public static string WorkArea() { var r = new RECT(); SystemParametersInfo(0x0030, 0, ref r, 0); return string.Format("{0}x{1}", r.R - r.L, r.B - r.T); }
  public static string RectOf(IntPtr h) { RECT r; GetWindowRect(h, out r); return string.Format("{0},{1} {2}x{3}", r.L, r.T, r.R-r.L, r.B-r.T); }
  public static int RectBottom(IntPtr h) { RECT r; GetWindowRect(h, out r); return r.B; }
  public static void Click(int x, int y) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(150);
    mouse_event(0x02,0,0,0,UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x04,0,0,0,UIntPtr.Zero);
  }
  public static bool TaskbarVisible() { var h = FindWindow("Shell_TrayWnd", IntPtr.Zero); return h != IntPtr.Zero && IsWindowVisible(h); }
}
'@

$results = New-Object System.Collections.Generic.List[string]

function Pass([string]$m) { $script:results.Add("PASS  $m") | Out-Null; Write-Host "  PASS  $m" -ForegroundColor Green }
function Fail([string]$m) { $script:results.Add("FAIL  $m") | Out-Null; Write-Host "  FAIL  $m" -ForegroundColor Red }
function Section([string]$m) { $script:results.Add("") | Out-Null; $script:results.Add("## $m") | Out-Null; Write-Host ""; Write-Host "== $m ==" -ForegroundColor Cyan }

function Save-Shot([string]$name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $bitmap.Save((Join-Path $shotDir $name), [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    return $bitmap
}

function Get-Probe {
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $json = Get-Content $uiPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if ($json.dock) { return $json }
        } catch { }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Find-Running([string]$pattern) {
    for ($i = 0; $i -lt 30; $i++) {
        try {
            $json = Get-Content $uiPath -Raw -Encoding UTF8 | ConvertFrom-Json
            $item = @($json.running) | Where-Object { "$($_.Path)" -match $pattern } | Select-Object -First 1
            if ($item) { return $item }
        } catch { }
        Start-Sleep -Milliseconds 200
    }
    return $null
}

function Stop-App {
    if (Get-Process PhoneDesktop -ErrorAction SilentlyContinue) {
        $exitProcess = Start-Process -FilePath $exe -ArgumentList '--exit' -PassThru
        $exitProcess.WaitForExit(5000) | Out-Null
        Start-Sleep -Milliseconds 1500
        if (Get-Process PhoneDesktop -ErrorAction SilentlyContinue) {
            Get-Process PhoneDesktop | Stop-Process -Force
            Start-Sleep -Milliseconds 600
        }
    }
}

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds

# ---------------------------------------------------------------- 准备
Stop-App
Get-Process notepad -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600
Remove-Item $uiPath -ErrorAction SilentlyContinue

$notepad = Start-Process notepad -PassThru
Start-Sleep -Milliseconds 1200
Start-Process -FilePath $exe -ArgumentList '--dev' | Out-Null
Start-Sleep -Milliseconds 2500
$probe = Get-Probe
if (-not $probe) { throw '未获取到 UI 探针' }
(New-Object -ComObject Shell.Application).ToggleDesktop()
Start-Sleep -Milliseconds 1500

Section '工作区铺满（消除最大化时的空白条）'
$workArea = [PDRD]::WorkArea()
if ($workArea -eq "$($screen.Width)x$($screen.Height)") {
    Pass "运行期工作区已扩到整屏：$workArea"
} else {
    Fail "运行期工作区未铺满：$workArea（屏幕 $($screen.Width)x$($screen.Height)）"
}

Section '运行中的应用出现在 Dock'
$item = Find-Running 'notepad'
if ($item) {
    Pass "记事本出现在 Dock 运行区（状态 $($item.State)，标题「$($item.Title)」）"
} else {
    Fail '记事本未出现在 Dock 运行区'
}

$shot = Save-Shot '60-dock-running.png'
$shot.Dispose()
Pass "60 Dock 运行区截图：$(Join-Path $shotDir '60-dock-running.png')"

Section '点击运行项：激活 / 最小化'
if ($item) {
    [void][PDRD]::ShowWindow($notepad.MainWindowHandle, 6)   # 先最小化
    Start-Sleep -Milliseconds 1200
    $item = Find-Running 'notepad'
    if ($item.State -eq 'minimized') {
        Pass '记事本最小化后，Dock 中状态为 minimized'
    } else {
        Fail "最小化后状态异常：$($item.State)"
    }

    $x = [int]($item.X + $item.Width / 2)
    $y = [int]($item.Y + $item.Height / 2)
    [PDRD]::Click($x, $y)
    Start-Sleep -Milliseconds 1200
    $item = Find-Running 'notepad'
    $notepad.Refresh()
    if ($item.State -eq 'active' -and [PDRD]::GetForegroundWindow() -eq $notepad.MainWindowHandle) {
        Pass '点击运行项：窗口恢复并取得前台（Dock 高亮为 active）'
    } else {
        Fail "点击后未激活：state=$($item.State)"
    }

    [void][PDRD]::SetWindowPos($notepad.MainWindowHandle, [IntPtr]::Zero, 160, 160, 760, 520, 0x0004)
    Start-Sleep -Milliseconds 400
    $item = Find-Running 'notepad'
    $x = [int]($item.X + $item.Width / 2)
    $y = [int]($item.Y + $item.Height / 2)
    [PDRD]::Click($x, $y)
    Start-Sleep -Milliseconds 1200
    $item = Find-Running 'notepad'
    if ($item.State -eq 'minimized') {
        Pass '再次点击运行项：窗口最小化'
    } else {
        Fail "再次点击后未最小化：state=$($item.State)"
    }
}

Section '最大化窗口铺满整屏（原任务栏位置无空白）'
[void][PDRD]::ShowWindow($notepad.MainWindowHandle, 3)
Start-Sleep -Milliseconds 900
$bottom = [PDRD]::RectBottom($notepad.MainWindowHandle)
if ($bottom -ge $screen.Height) {
    Pass "最大化窗口底边 $bottom ≥ 屏高 $($screen.Height)"
} else {
    Fail "最大化窗口底边 $bottom 未铺满屏高 $($screen.Height)"
}

$bitmap = Save-Shot '61-maximized-fullscreen.png'
$pixel = $bitmap.GetPixel([int]($screen.Width / 2), $screen.Height - 15)
$bitmap.Dispose()
$isWallpaper = ($pixel.R -eq 255 -and $pixel.G -eq 227 -and $pixel.B -eq 0)
if (-not $isWallpaper) {
    Pass "原任务栏位置像素为窗口内容（$($pixel.R),$($pixel.G),$($pixel.B)），无空白条"
} else {
    Fail '原任务栏位置仍是壁纸色（空白条仍在）'
}
Pass "61 最大化截图：$(Join-Path $shotDir '61-maximized-fullscreen.png')"

Section '退出后恢复工作区'
[void][PDRD]::ShowWindow($notepad.MainWindowHandle, 9)
Start-Sleep -Milliseconds 300
$notepad.CloseMainWindow() | Out-Null
Start-Sleep -Milliseconds 600
Stop-App

$workAreaAfter = [PDRD]::WorkArea()
if ($workAreaAfter -ne "$($screen.Width)x$($screen.Height)") {
    Pass "退出后工作区已恢复：$workAreaAfter"
} else {
    Fail "退出后工作区仍是整屏：$workAreaAfter"
}

if ([PDRD]::TaskbarVisible()) {
    Pass '退出后任务栏已恢复'
} else {
    Fail '退出后任务栏未恢复'
}

# ---------------------------------------------------------------- 报告
$failCount = ($results | Where-Object { $_ -like 'FAIL*' }).Count
$summary = @()
$summary += '# Dock 任务栏语义验收报告'
$summary += ''
$summary += "时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$summary += "可执行文件：$exe"
$summary += "截图目录：$shotDir"
$summary += ''
$summary += $results
$summary += ''
if ($failCount -eq 0) { $summary += '结论：全部通过' } else { $summary += "结论：$failCount 项失败" }
$summary -join [Environment]::NewLine | Out-File -FilePath $reportPath -Encoding UTF8
Write-Host ""
Write-Host "报告已写入：$reportPath" -ForegroundColor Cyan
if ($failCount -gt 0) { exit 1 }
