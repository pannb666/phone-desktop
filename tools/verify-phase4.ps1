[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipDrag,
    [switch]$SkipHotkey
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\PhoneDesktop\bin\$Configuration\net8.0-windows\PhoneDesktop.exe"
$shotDir = Join-Path $root 'artifacts\shots\phase4'
$reportPath = Join-Path $root 'artifacts\verify-phase4-report.txt'
$appData = Join-Path $env:APPDATA 'PhoneDesktop'
$overlayUiPath = Join-Path $appData 'dev-ui.json'
$drawerUiPath = Join-Path $appData 'dev-drawer.json'
$settingsPath = Join-Path $appData 'settings.json'
$logPath = Join-Path $appData 'log.txt'

if (-not (Test-Path $exe)) { throw "未找到 $exe，请先运行 .\build.ps1" }

New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportPath) | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PD4 {
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowW")] public static extern IntPtr FindWindow(string c, IntPtr w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowExW")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

  public static bool IsUnder(IntPtr child, IntPtr ancestor) {
    var h = child; var guard = 0;
    while (h != IntPtr.Zero && guard++ < 12) { if (h == ancestor) return true; h = GetParent(h); }
    return false;
  }
  public static bool DesktopVisible() {
    var progman = FindWindow("Progman", IntPtr.Zero);
    var p = new POINT(); p.X = 640; p.Y = 300; var hit = WindowFromPoint(p);
    if (hit == IntPtr.Zero) return false;
    var h = hit; var guard = 0;
    while (h != IntPtr.Zero && guard++ < 12) {
      if (h == progman) return true;
      var sb = new StringBuilder(256); GetClassName(h, sb, 256);
      if (sb.ToString().StartsWith("HwndWrapper[PhoneDesktop")) return true;
      if (sb.ToString() == "WorkerW") return true;
      h = GetParent(h);
    }
    return false;
  }
  public static IntPtr Drawer() { return FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "PhoneDesktopDrawer"); }
  public static IntPtr Settings() { return FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, "PhoneDesktopSettings"); }
  public static void Click(int x, int y, bool right = false) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
    if (right) { mouse_event(0x08, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x10, 0, 0, 0, UIntPtr.Zero); }
    else { mouse_event(0x02, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x04, 0, 0, 0, UIntPtr.Zero); }
  }
  public static void Drag(int x1, int y1, int x2, int y2) {
    SetCursorPos(x1, y1); System.Threading.Thread.Sleep(150);
    mouse_event(0x02, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(150);
    for (var i = 1; i <= 16; i++) { SetCursorPos(x1 + (x2 - x1) * i / 16, y1); System.Threading.Thread.Sleep(30); }
    System.Threading.Thread.Sleep(150);
    mouse_event(0x04, 0, 0, 0, UIntPtr.Zero);
  }
  public static void CtrlSpace() {
    keybd_event(0x11, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x20, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x20, 0, 2, UIntPtr.Zero); System.Threading.Thread.Sleep(60);
    keybd_event(0x11, 0, 2, UIntPtr.Zero);
  }
  public static bool TaskbarVisible() {
    var h = FindWindow("Shell_TrayWnd", IntPtr.Zero);
    return h != IntPtr.Zero && IsWindowVisible(h);
  }
}
'@

$results = New-Object System.Collections.Generic.List[string]
$script:toggled = $false

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
    for ($i = 0; $i -lt 5; $i++) {
        if ([PD4]::DesktopVisible()) { return $true }
        $shell = New-Object -ComObject Shell.Application
        $shell.ToggleDesktop()
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        $script:toggled = $true
        Start-Sleep -Milliseconds 1000
    }
    return [PD4]::DesktopVisible()
}

function Restore-DesktopState {
    if (-not $script:toggled) { return }
    $shell = New-Object -ComObject Shell.Application
    $shell.ToggleDesktop()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    Start-Sleep -Milliseconds 600
}

function Stop-App {
    if (Get-Process PhoneDesktop -ErrorAction SilentlyContinue) {
        $exitProcess = Start-Process -FilePath $exe -ArgumentList '--exit' -PassThru
        $exitProcess.WaitForExit(5000) | Out-Null
        Start-Sleep -Milliseconds 700
        if (Get-Process PhoneDesktop -ErrorAction SilentlyContinue) {
            Get-Process PhoneDesktop | Stop-Process -Force
            Start-Sleep -Milliseconds 500
        }
    }
}

function Start-App {
    Start-Process -FilePath $exe -ArgumentList '--dev' | Out-Null
    Start-Sleep -Milliseconds 900
    Wait-OverlayUi 12000 | Out-Null
}

function Wait-OverlayUi([int]$timeoutMs = 10000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-Path $overlayUiPath) {
            try {
                $json = Get-Content $overlayUiPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($json.dock -and $json.settings) { return $json }
            } catch { }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Get-OverlayUi {
    return (Get-Content $overlayUiPath -Raw -Encoding UTF8 | ConvertFrom-Json)
}

function Open-Drawer($ui) {
    Remove-Item $drawerUiPath -ErrorAction SilentlyContinue
    $button = $ui.drawerButton
    [PD4]::Click([int]($button.X + $button.Width / 2), [int]($button.Y + $button.Height / 2))
    return (Wait-DrawerUi 8000)
}

function Wait-DrawerUi([int]$timeoutMs = 8000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-Path $drawerUiPath) {
            try {
                $json = Get-Content $drawerUiPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($json.window -and $json.items -and $json.total -gt 0) { return $json }
            } catch { }
        }
        Start-Sleep -Milliseconds 120
    }
    return $null
}

function Close-DrawerIfOpen {
    if ([PD4]::IsWindowVisible([PD4]::Drawer())) {
        [PD4]::Click(200, 60)
        Start-Sleep -Milliseconds 700
    }
}

# 用 UI Automation 精确触发右键菜单项（比按坐标点菜单更可靠）
function Invoke-MenuItem([string]$name, [int]$timeoutMs = 4000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        $condition = New-Object System.Windows.Automation.PropertyCondition ([System.Windows.Automation.AutomationElement]::NameProperty), $name
        $element = [System.Windows.Automation.AutomationElement]::RootElement.FindFirst(
            [System.Windows.Automation.TreeScope]::Descendants, $condition)
        if ($element) {
            try {
                $pattern = $element.GetCurrentPattern([System.Windows.Automation.InvokePattern]::Pattern)
                if ($pattern) { $pattern.Invoke(); return $true }
            } catch { }
        }
        Start-Sleep -Milliseconds 120
    }
    return $false
}

function Get-SettingsDock {
    if (-not (Test-Path $settingsPath)) { return @() }
    return @((Get-Content $settingsPath -Raw -Encoding UTF8 | ConvertFrom-Json).Dock | ForEach-Object { $_.Name })
}

# ---------------------------------------------------------------- 准备
Stop-App
Close-DrawerIfOpen
$backupPath = Join-Path $env:TEMP 'PhoneDesktop-settings-phase4-backup.json'
if (Test-Path $settingsPath) { Copy-Item $settingsPath $backupPath -Force }
Remove-Item $settingsPath, $overlayUiPath, $drawerUiPath, $logPath -ErrorAction SilentlyContinue

Section '阶段 4 验收：Dock 固定 / 取消固定 / 拖拽 / 快捷键 / 设置页 / 自启'
Start-App
Ensure-DesktopVisible | Out-Null
Start-Sleep -Milliseconds 800
$ui = Get-OverlayUi
$defaultCount = @($ui.items).Count
if ($defaultCount -ge 3) {
    Pass "初始 Dock 项：$defaultCount"
} else {
    Fail "初始 Dock 项异常：$defaultCount"
}

# ---------------------------------------------------------------- 固定
Section '从抽屉固定到 Dock'
$drawerUi = Open-Drawer $ui
if (-not $drawerUi) {
    Fail '抽屉未打开'
} else {
    $tile = $drawerUi.items[0]
    $tx = [int]($tile.x + $tile.w / 2)
    $ty = [int]($tile.y + $tile.h / 2)
    [PD4]::Click($tx, $ty, $true)
    Start-Sleep -Milliseconds 700
    $pinned = Invoke-MenuItem '固定到 Dock'
    Start-Sleep -Milliseconds 900

    $names = Get-SettingsDock
    if ($pinned -and ($names -contains $tile.name)) {
        Pass "右键「固定到 Dock」成功：$($tile.name)（共 $($names.Count) 项）"
    } else {
        Fail "固定失败：menu=$pinned, dock=$($names -join ',')"
    }

    $shot = Save-Screenshot '30-drawer-pin-menu.png'
    $shot.Bitmap.Dispose()
    Pass "30 抽屉右键菜单截图：$(Join-Path $shotDir '30-drawer-pin-menu.png')"
}

Close-DrawerIfOpen
Start-Sleep -Milliseconds 600

# ---------------------------------------------------------------- 取消固定
Section 'Dock 右键取消固定'
$ui = Get-OverlayUi
$last = $ui.items[@($ui.items).Count - 1]
$lx = [int]($last.X + $last.Width / 2)
$ly = [int]($last.Y + $last.Height / 2)
[PD4]::Click($lx, $ly, $true)
Start-Sleep -Milliseconds 700
$unpinned = Invoke-MenuItem '取消固定'
Start-Sleep -Milliseconds 900

$namesAfterUnpin = Get-SettingsDock
if ($unpinned -and ($namesAfterUnpin -notcontains $last.Name)) {
    Pass "右键「取消固定」成功：$($last.Name)（剩余 $($namesAfterUnpin.Count) 项）"
} else {
    Fail "取消固定失败：menu=$unpinned, dock=$($namesAfterUnpin -join ',')"
}

$shot = Save-Screenshot '31-dock-context.png'
$shot.Bitmap.Dispose()
Pass "31 Dock 右键菜单截图：$(Join-Path $shotDir '31-dock-context.png')"

# ---------------------------------------------------------------- 拖拽排序
if (-not $SkipDrag) {
    Section 'Dock 拖拽排序'
    $ui = Get-OverlayUi
    $items = @($ui.items)
    $first = $items[0]
    $lastItem = $items[$items.Count - 1]
    $fx = [int]($first.X + $first.Width / 2)
    $fy = [int]($first.Y + $first.Height / 2)
    $lxp = [int]($lastItem.X + $lastItem.Width / 2)
    $lyp = [int]($lastItem.Y + $lastItem.Height / 2)

    [PD4]::Drag($lxp, $lyp, $fx, $fy)
    Start-Sleep -Milliseconds 1000

    $orderAfterDrag = Get-SettingsDock
    if ($orderAfterDrag[0] -eq $lastItem.Name) {
        Pass "拖拽排序生效：$($lastItem.Name) 移到首位"
    } else {
        Fail "拖拽排序失败：$($orderAfterDrag -join ' | ')"
    }

    Stop-App
    Start-App
    Ensure-DesktopVisible | Out-Null
    Start-Sleep -Milliseconds 900
    $uiReloaded = Get-OverlayUi
    $orderAfterRestart = @($uiReloaded.items | ForEach-Object { $_.Name })
    if (($orderAfterRestart -join '|') -eq ($orderAfterDrag -join '|')) {
        Pass "重启后顺序保持：$($orderAfterRestart -join ' → ')"
    } else {
        Fail "重启后顺序不一致：$($orderAfterRestart -join ' → ')"
    }

    $shot = Save-Screenshot '32-dock-reordered.png'
    $shot.Bitmap.Dispose()
    Pass "32 拖拽排序截图：$(Join-Path $shotDir '32-dock-reordered.png')"
}

# ---------------------------------------------------------------- 设置页
Section '设置页'
$ui = Get-OverlayUi
$gear = $ui.settings
if (-not $gear) {
    Fail '探针中没有设置按钮位置'
} else {
    [PD4]::Click([int]($gear.X + $gear.Width / 2), [int]($gear.Y + $gear.Height / 2))
    Start-Sleep -Milliseconds 1200

    $settingsHandle = [PD4]::Settings()
    if ($settingsHandle -ne [IntPtr]::Zero -and [PD4]::IsWindowVisible($settingsHandle)) {
        Pass '点击 Dock 齿轮按钮打开设置页'
        $rect = New-Object PD4+RECT
        [void][PD4]::GetWindowRect($settingsHandle, [ref]$rect)
        Pass "设置窗口尺寸：$($rect.R - $rect.L)x$($rect.B - $rect.T)"

        $shot = Save-Screenshot '33-settings.png'
        $shot.Bitmap.Dispose()
        Pass "33 设置页截图：$(Join-Path $shotDir '33-settings.png')"

        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 700
        if (-not [PD4]::IsWindowVisible([PD4]::Settings())) {
            Pass 'Esc 关闭设置页'
        } else {
            Fail 'Esc 未关闭设置页'
        }
    } else {
        Fail '设置页没有打开'
    }
}

# ---------------------------------------------------------------- 全局快捷键
if (-not $SkipHotkey) {
    Section '全局快捷键'
    $notepad = Start-Process notepad -PassThru
    Start-Sleep -Milliseconds 1400
    $notepad.Refresh()
    if ($notepad.MainWindowHandle -ne [IntPtr]::Zero) {
        [void][PD4]::SetForegroundWindow($notepad.MainWindowHandle)
        Start-Sleep -Milliseconds 500

        [PD4]::CtrlSpace()
        Start-Sleep -Milliseconds 1600

        if ([PD4]::IsWindowVisible([PD4]::Drawer())) {
            Pass '普通窗口在前台时，Ctrl+Space 可呼出应用抽屉'
        } else {
            Fail 'Ctrl+Space 未呼出抽屉'
        }

        if ([PD4]::GetForegroundWindow() -eq [PD4]::Drawer()) {
            Pass '抽屉呼出后取得前台（可直接输入搜索）'
        } else {
            Fail '抽屉未取得前台'
        }

        $shot = Save-Screenshot '34-hotkey-drawer.png'
        $shot.Bitmap.Dispose()
        Pass "34 快捷键呼出截图：$(Join-Path $shotDir '34-hotkey-drawer.png')"

        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 600
    } else {
        Fail '记事本未启动'
    }

    [void]$notepad.CloseMainWindow()
    Start-Sleep -Milliseconds 500
    if (-not $notepad.HasExited) { $notepad | Stop-Process -Force -ErrorAction SilentlyContinue }
}

# ---------------------------------------------------------------- 开机自启
Section '开机自启'
$runValue = (Get-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name PhoneDesktop -ErrorAction SilentlyContinue).PhoneDesktop
if ($runValue -and $runValue.Contains('PhoneDesktop.exe')) {
    Pass "开机自启已写入注册表：$runValue"
} else {
    Fail "开机自启未写入：$runValue"
}

# ---------------------------------------------------------------- 收尾
Stop-App
if (-not (Get-Process PhoneDesktop -ErrorAction SilentlyContinue)) {
    Pass '退出后进程结束，无残留'
} else {
    Fail '进程未退出'
    Get-Process PhoneDesktop | Stop-Process -Force
}

if (Test-Path $backupPath) {
    Copy-Item $backupPath $settingsPath -Force
    Remove-Item $backupPath -ErrorAction SilentlyContinue
}

Restore-DesktopState

# ---------------------------------------------------------------- 报告
$failCount = ($results | Where-Object { $_ -like 'FAIL*' }).Count
$summary = @()
$summary += '# PhoneDesktop 阶段 4 验收报告'
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
Write-Host ""
Write-Host "报告已写入：$reportPath" -ForegroundColor Cyan

if ($failCount -gt 0) { exit 1 }
