[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipLaunchTest,

    [switch]$SkipPersistenceTest
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\PhoneDesktop\bin\$Configuration\net8.0-windows\PhoneDesktop.exe"
$shotDir = Join-Path $root 'artifacts\shots\phase2'
$reportPath = Join-Path $root 'artifacts\verify-phase2-report.txt'
$appData = Join-Path $env:APPDATA 'PhoneDesktop'
$devUiPath = Join-Path $appData 'dev-ui.json'
$logPath = Join-Path $appData 'log.txt'
$settingsPath = Join-Path $appData 'settings.json'

if (-not (Test-Path $exe)) {
    throw "未找到 $exe，请先运行 .\build.ps1"
}

New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportPath) | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PD2 {
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowW")] public static extern IntPtr FindWindow(string c, IntPtr w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowExW")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
  public static string HitInfo() {
    var progman = FindWindow("Progman", IntPtr.Zero);
    var p = new POINT(); p.X = 1280; p.Y = 700;
    var hit = WindowFromPoint(p);
    var sb = new StringBuilder("hit=");
    var h = hit; var g = 0;
    while (h != IntPtr.Zero && g++ < 6) {
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      var txt = new StringBuilder(256); GetWindowText(h, txt, 256);
      sb.Append("0x").Append(h.ToInt64().ToString("X8")).Append("[").Append(cls).Append("|").Append(txt).Append("] -> ");
      h = GetParent(h);
    }
    sb.Append("progman=0x").Append(progman.ToInt64().ToString("X8"));
    return sb.ToString();
  }
  public static bool IsUnder(IntPtr child, IntPtr ancestor) {
    var h = child; var guard = 0;
    while (h != IntPtr.Zero && guard++ < 12) { if (h == ancestor) return true; h = GetParent(h); }
    return false;
  }  // 桌面层判定：Progman 链上，或 Explorer 临时把桌面子窗口挪到的 WorkerW 下，
  // 或直接命中我们的覆盖层（标题/类名以 HwndWrapper[PhoneDesktop 开头）。
  public static bool IsDesktopSurface(IntPtr hit, IntPtr progman) {
    var h = hit; var guard = 0;
    while (h != IntPtr.Zero && guard++ < 12) {
      if (h == progman) return true;
      var cls = new StringBuilder(256); GetClassName(h, cls, 256);
      var name = cls.ToString();
      if (name.StartsWith("HwndWrapper[PhoneDesktop", StringComparison.Ordinal)) return true;
      if (name == "WorkerW") return true;
      h = GetParent(h);
    }
    return false;
  }
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public static bool DesktopVisible() {
    var progman = FindWindow("Progman", IntPtr.Zero);
    var p = new POINT(); p.X = 1280; p.Y = 700;
    var hit = WindowFromPoint(p);
    return hit != IntPtr.Zero && IsDesktopSurface(hit, progman);
  }
  public static bool TaskbarVisible() {
    var h = FindWindow("Shell_TrayWnd", IntPtr.Zero);
    return h != IntPtr.Zero && IsWindowVisible(h);
  }
  public static IntPtr Overlay() {
    var progman = FindWindow("Progman", IntPtr.Zero);
    var overlay = FindWindowEx(progman, IntPtr.Zero, null, "PhoneDesktopOverlay");
    if (overlay != IntPtr.Zero) return overlay;
    var worker = IntPtr.Zero;
    while ((worker = FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero) {
      var child = FindWindowEx(worker, IntPtr.Zero, null, "PhoneDesktopOverlay");
      if (child != IntPtr.Zero) return child;
    }
    return IntPtr.Zero;
  }
}
'@

$results = New-Object System.Collections.Generic.List[string]

# 拖拽与持久化测试会改动 Dock 顺序，先备份用户配置，结束时还原
$settingsBackup = Join-Path $env:TEMP 'PhoneDesktop-phase2-settings-backup.json'
$settingsPath = Join-Path $env:APPDATA 'PhoneDesktop\settings.json'
if (Test-Path $settingsPath) { Copy-Item $settingsPath $settingsBackup -Force }
$script:toggledDesktop = $false

function Pass([string]$m) { $script:results.Add("PASS  $m") | Out-Null; Write-Host "  PASS  $m" -ForegroundColor Green }
function Fail([string]$m) { $script:results.Add("FAIL  $m") | Out-Null; Write-Host "  FAIL  $m" -ForegroundColor Red }
function Section([string]$m) { $script:results.Add("") | Out-Null; $script:results.Add("## $m") | Out-Null; Write-Host ""; Write-Host "== $m ==" -ForegroundColor Cyan }

function Save-Screenshot([string]$name) {
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $bitmap = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
    $graphics = [System.Drawing.Graphics]::FromImage($bitmap)
    $graphics.CopyFromScreen($bounds.Location, [System.Drawing.Point]::Empty, $bounds.Size)
    $path = Join-Path $shotDir $name
    $bitmap.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
    $graphics.Dispose()
    return [pscustomobject]@{ Bitmap = $bitmap; Path = $path }
}

function Ensure-DesktopVisible {
    for ($i = 0; $i -lt 4; $i++) {
        if ([PD2]::DesktopVisible()) { return $true }
        $shell = New-Object -ComObject Shell.Application
        $shell.ToggleDesktop()
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        $script:toggledDesktop = $true
        Start-Sleep -Milliseconds 900
    }
    return [PD2]::DesktopVisible()
}

function Restore-DesktopState {
    if (-not $script:toggledDesktop) { return }
    $shell = New-Object -ComObject Shell.Application
    $shell.ToggleDesktop()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    Start-Sleep -Milliseconds 600
}

function Stop-App {
    $process = Get-Process PhoneDesktop -ErrorAction SilentlyContinue
    if ($process) {
        # 必须等 --exit 进程自己结束：它是异步启动的，晚到会把新实例一起带走
        $exitProcess = Start-Process -FilePath $exe -ArgumentList '--exit' -PassThru
        $exitProcess.WaitForExit(5000) | Out-Null
        Start-Sleep -Milliseconds 600
        if (Get-Process PhoneDesktop -ErrorAction SilentlyContinue) {
            Get-Process PhoneDesktop | Stop-Process -Force
            Start-Sleep -Milliseconds 500
        }
    }
}

function Click-At([int]$x, [int]$y, [string]$button = 'left') {
    [void][PD2]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 90
    switch ($button) {
        'left' {
            [PD2]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 50
            [PD2]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)
        }
        'right' {
            [PD2]::mouse_event(0x08, 0, 0, 0, [UIntPtr]::Zero)
            Start-Sleep -Milliseconds 50
            [PD2]::mouse_event(0x10, 0, 0, 0, [UIntPtr]::Zero)
        }
    }
}

function Wait-DevUi([int]$timeoutMs = 8000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-Path $devUiPath) {
            try {
                $json = Get-Content $devUiPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($json.dock -and $json.items) {
                    $age = (Get-Date) - (Get-Item $devUiPath).LastWriteTime
                    if ($age.TotalSeconds -lt 20) { return $json }
                }
            } catch {
                # 文件可能正在写入
            }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Start-App([string]$taskManagerOverride = $null) {
    if ($taskManagerOverride) {
        $env:PHONEDESKTOP_TASKMGR = $taskManagerOverride
    } else {
        Remove-Item Env:\PHONEDESKTOP_TASKMGR -ErrorAction SilentlyContinue
    }

    $appProcess = Start-Process -FilePath $exe -ArgumentList '--dev' -PassThru
    Start-Sleep -Milliseconds 900
    Remove-Item Env:\PHONEDESKTOP_TASKMGR -ErrorAction SilentlyContinue
}

function Get-Center($rect) {
    return @([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
}

# ---------------------------------------------------------------- 准备
Stop-App
Remove-Item $logPath, $devUiPath -ErrorAction SilentlyContinue

Section '阶段 2 验收：Dock 布局'
Start-App 'notepad.exe'
$devUi = Wait-DevUi 10000
if (-not $devUi) {
    Fail '未获取到 dev-ui.json，Dock 可能没有渲染'
    throw '阶段 2 验收中止'
}
Ensure-DesktopVisible | Out-Null
Start-Sleep -Milliseconds 600

$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$dock = $devUi.dock
$taskManager = $devUi.taskManager
$exit = $devUi.exit
$items = @($devUi.items)

$dockCenter = $dock.X + $dock.Width / 2
if ([Math]::Abs($dockCenter - $screen.Width / 2) -le 3) {
    Pass "Dock 水平居中（中心 X=$([int]$dockCenter)，屏宽 $($screen.Width)）"
} else {
    Fail "Dock 未居中：中心 X=$([int]$dockCenter)"
}

$dockBottom = $dock.Y + $dock.Height
$taskbarHidden = -not [PD2]::TaskbarVisible()
if ($taskbarHidden) {
    $gap = $screen.Height - $dockBottom
    if ($gap -ge 10 -and $gap -le 40) {
        Pass "Windows 任务栏已被隐藏，Dock 贴屏幕底部（底边 $([int]$dockBottom)，间距 $([int]$gap) px）"
    } else {
        Fail "任务栏已隐藏但 Dock 未贴底：底边 $([int]$dockBottom)，屏高 $($screen.Height)"
    }
} else {
    $gap = $work.Bottom - $dockBottom
    if ($dockBottom -le $work.Bottom -and $gap -ge 0 -and $gap -le 40) {
        Pass "Dock 位于任务栏之上（底边 $([int]$dockBottom)，工作区底 $($work.Bottom)，间距 $([int]$gap) px）"
    } else {
        Fail "Dock 与任务栏/工作区关系异常：dock 底 $([int]$dockBottom)，工作区底 $($work.Bottom)"
    }
}

if ($taskbarHidden) {
    Pass '运行期间已隐藏 Windows 任务栏'
} else {
    Fail '运行期间 Windows 任务栏仍然显示'
}

if ($items.Count -gt 0) {
    Pass "Dock 固定项数量：$($items.Count)"
} else {
    Fail 'Dock 没有固定项'
}

if ($taskManager -and $items.Count -gt 0 -and $taskManager.X -lt $items[0].X) {
    Pass '任务管理器按钮位于固定项左侧'
} else {
    Fail '任务管理器按钮位置异常'
}

if ($exit -and $items.Count -gt 0 -and $exit.X -gt $items[$items.Count - 1].X) {
    Pass '退出按钮位于最右侧'
} else {
    Fail '退出按钮不在最右侧'
}

$shot = Save-Screenshot '10-dock.png'
Pass "10 Dock 截图：$($shot.Path)"
$shot.Bitmap.Dispose()

# ---------------------------------------------------------------- 任务管理器
if (-not $SkipLaunchTest) {
    Section '任务管理器按钮'
    $before = @(Get-Process notepad -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
    $center = Get-Center $taskManager
    Click-At $center[0] $center[1] 'left'
    Start-Sleep -Milliseconds 1200
    $after = @(Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
    if ($after.Count -gt 0) {
        Pass "任务管理器按钮左键触发启动（测试用命令 notepad.exe，新进程 PID $($after[0].Id)）"
        foreach ($process in $after) {
            $process.CloseMainWindow() | Out-Null
            Start-Sleep -Milliseconds 200
            if (-not $process.HasExited) { $process | Stop-Process -Force -ErrorAction SilentlyContinue }
        }
    } else {
        Fail '任务管理器按钮点击后没有启动进程'
    }
}

# ---------------------------------------------------------------- 退出按钮
Section '退出按钮（右键一次提示 / 右键双击退出）'
# Dock 会随“运行中的应用”增减而变宽，点击前必须重新读探针拿最新坐标
function Get-ExitCenterFresh {
    try {
        $fresh = Get-Content $uiPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($fresh.exit) { return Get-Center $fresh.exit }
    } catch { }
    return Get-Center $exit
}

# Dock 会随运行项增减而变宽，右键可能落到空白处；这里带重试，并在重试前关掉误开的菜单
$hintShown = $false
for ($attempt = 0; $attempt -lt 4 -and -not $hintShown; $attempt++) {
    if ($attempt -gt 0) {
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 300
    }

    $exitCenter = Get-ExitCenterFresh
    Click-At $exitCenter[0] $exitCenter[1] 'right'
    Start-Sleep -Milliseconds 500

    $logText = Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue
    if (($logText -join "`n") -match '再右键一次退出') {
        $hintShown = $true
    }
}

$shot = Save-Screenshot '11-exit-hint.png'
$shot.Bitmap.Dispose()
Pass "11 右键一次后的提示截图：$(Join-Path $shotDir '11-exit-hint.png')"

if ($hintShown) {
    Pass '右键一次显示“再右键一次退出”提示'
} else {
    Fail '右键一次未显示退出提示'
}
if (Get-Process PhoneDesktop -ErrorAction SilentlyContinue) {
    Pass '右键一次后进程仍在运行（未误退出）'
} else {
    Fail '右键一次就退出了（应等待第二次）'
}

Start-Sleep -Milliseconds 400
$exitCenter = Get-ExitCenterFresh
Click-At $exitCenter[0] $exitCenter[1] 'right'
Start-Sleep -Milliseconds 120
$exitCenter = Get-ExitCenterFresh
Click-At $exitCenter[0] $exitCenter[1] 'right'

$waited = 0
while ((Get-Process PhoneDesktop -ErrorAction SilentlyContinue) -and $waited -lt 5000) {
    Start-Sleep -Milliseconds 100
    $waited += 100
}

if (-not (Get-Process PhoneDesktop -ErrorAction SilentlyContinue)) {
    Pass '右键双击退出成功（进程结束）'
} else {
    Fail '右键双击后进程未退出'
    Get-Process PhoneDesktop | Stop-Process -Force
}

Start-Sleep -Milliseconds 900
if (-not [PD2]::IsWindow([PD2]::Overlay())) {
    Pass '退出后覆盖层窗口销毁'
} else {
    Fail '退出后覆盖层窗口仍存在'
}

if ([PD2]::TaskbarVisible()) {
    Pass '退出后 Windows 任务栏已恢复'
} else {
    Fail '退出后 Windows 任务栏未恢复'
}

$shot = Save-Screenshot '12-desktop-restored.png'
$shot.Bitmap.Dispose()
Pass "12 退出后桌面截图：$(Join-Path $shotDir '12-desktop-restored.png')"

# 左键单击也能退出（按实际使用反馈新增的入口）
Section '退出按钮（左键单击）'
Start-App 'notepad.exe'
$uiLeft = Wait-DevUi 12000
if (-not $uiLeft) {
    Fail '重启后未获取到 UI 探针'
} else {
    Ensure-DesktopVisible | Out-Null
    Start-Sleep -Milliseconds 500
    $leftCenter = Get-Center $uiLeft.exit
    Click-At $leftCenter[0] $leftCenter[1] 'left'
    $waitedLeft = 0
    while ((Get-Process PhoneDesktop -ErrorAction SilentlyContinue) -and $waitedLeft -lt 5000) {
        Start-Sleep -Milliseconds 100
        $waitedLeft += 100
    }
    if (-not (Get-Process PhoneDesktop -ErrorAction SilentlyContinue)) {
        Pass '左键单击退出成功（进程结束）'
    } else {
        Fail '左键单击后进程未退出'
        Get-Process PhoneDesktop | Stop-Process -Force
    }
    Start-Sleep -Milliseconds 800
    if ([PD2]::TaskbarVisible()) {
        Pass '左键退出后任务栏恢复'
    } else {
        Fail '左键退出后任务栏未恢复'
    }
}

# ---------------------------------------------------------------- 持久化
if (-not $SkipPersistenceTest) {
    Section 'Dock 列表持久化'
    $backupPath = Join-Path $env:TEMP 'PhoneDesktop-settings-backup.json'
    Copy-Item $settingsPath $backupPath -Force

    try {
        $settings = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $originalOrder = @($settings.Dock | ForEach-Object { $_.Name })
        $reversed = @($settings.Dock)
        [array]::Reverse($reversed)
        $settings.Dock = $reversed
        $expected = @($settings.Dock | ForEach-Object { $_.Name })
        $settings | ConvertTo-Json -Depth 8 | Set-Content $settingsPath -Encoding UTF8

        Remove-Item $devUiPath -ErrorAction SilentlyContinue
        Start-App
        $devUi2 = Wait-DevUi 10000
        if (-not $devUi2) {
            Fail '重启后未获取到 dev-ui.json'
        } else {
            Ensure-DesktopVisible | Out-Null
            $actual = @($devUi2.items | ForEach-Object { $_.Name })
            if (($actual -join '|') -eq ($expected -join '|')) {
                Pass "重启后 Dock 顺序保持为 settings.json 中的顺序：$($actual -join ' → ')"
            } else {
                Fail "重启后 Dock 顺序不符：期望 $($expected -join ' → ')，实际 $($actual -join ' → ')"
            }

            $restoredSettings = Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json
            if (@($restoredSettings.Dock).Count -eq $originalOrder.Count) {
                Pass "Dock 项数量持久化正确（$($originalOrder.Count) 项）"
            } else {
                Fail 'Dock 项数量与预期不符'
            }
        }

        $shot = Save-Screenshot '13-dock-reordered.png'
        $shot.Bitmap.Dispose()
        Pass "13 重排后 Dock 截图：$(Join-Path $shotDir '13-dock-reordered.png')"

        Stop-App
    }
    finally {
        Copy-Item $backupPath $settingsPath -Force
        Remove-Item $backupPath -ErrorAction SilentlyContinue
    }
}

Restore-DesktopState

# ---------------------------------------------------------------- 报告
$failCount = ($results | Where-Object { $_ -like 'FAIL*' }).Count
$summary = @()
$summary += '# PhoneDesktop 阶段 2 验收报告'
$summary += ''
$summary += "时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$summary += "可执行文件：$exe"
$summary += "日志：$logPath"
$summary += "截图目录：$shotDir"
$summary += ''
$summary += $results
$summary += ''
if ($failCount -eq 0) { $summary += '结论：全部通过' } else { $summary += "结论：$failCount 项失败" }
$summary -join [Environment]::NewLine | Out-File -FilePath $reportPath -Encoding UTF8
if (Test-Path $settingsBackup) {
    Copy-Item $settingsBackup $settingsPath -Force
    Remove-Item $settingsBackup -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "报告已写入：$reportPath" -ForegroundColor Cyan

if ($failCount -gt 0) { exit 1 }
