[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipLaunchTest
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\PhoneDesktop\bin\$Configuration\net8.0-windows\PhoneDesktop.exe"
$shotDir = Join-Path $root 'artifacts\shots\phase3'
$reportPath = Join-Path $root 'artifacts\verify-phase3-report.txt'
$appData = Join-Path $env:APPDATA 'PhoneDesktop'
$overlayUiPath = Join-Path $appData 'dev-ui.json'
$drawerUiPath = Join-Path $appData 'dev-drawer.json'
$logPath = Join-Path $appData 'log.txt'

if (-not (Test-Path $exe)) { throw "未找到 $exe，请先运行 .\build.ps1" }

New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportPath) | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PD3 {
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowW")] public static extern IntPtr FindWindow(string c, IntPtr w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowExW")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
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
  public static IntPtr ByTitle(string title) { return FindWindowEx(IntPtr.Zero, IntPtr.Zero, null, title); }
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
  public static bool DesktopVisible() {
    var progman = FindWindow("Progman", IntPtr.Zero);
    var p = new POINT(); p.X = 1280; p.Y = 700;
    var hit = WindowFromPoint(p);
    return hit != IntPtr.Zero && IsDesktopSurface(hit, progman);
  }
  public static bool IsTopMost(IntPtr h) { return h != IntPtr.Zero && (GetWindowLongPtr(h, -20).ToInt64() & 0x8) != 0; }
  public static bool IsForeground(IntPtr h) { return GetForegroundWindow() == h; }
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
        if ([PD3]::DesktopVisible()) { return $true }
        $shell = New-Object -ComObject Shell.Application
        $shell.ToggleDesktop()
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        $script:toggled = $true
        Start-Sleep -Milliseconds 1000
    }
    return [PD3]::DesktopVisible()
}

function Restore-DesktopState {
    if (-not $script:toggled) { return }
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

function Start-App {
    Start-Process -FilePath $exe -ArgumentList '--dev' | Out-Null
    Start-Sleep -Milliseconds 900
}

function Click-At([int]$x, [int]$y, [string]$button = 'left') {
    [void][PD3]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 80
    if ($button -eq 'left') {
        [PD3]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero)
        Start-Sleep -Milliseconds 40
        [PD3]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)
    }
}

function DoubleClick-At([int]$x, [int]$y) {
    [void][PD3]::SetCursorPos($x, $y)
    Start-Sleep -Milliseconds 80
    [PD3]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 35
    [PD3]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 90
    [PD3]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 35
    [PD3]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)
}

function Get-Center($rect) {
    return @([int]($rect.X + $rect.Width / 2), [int]($rect.Y + $rect.Height / 2))
}

# 网格项在 dev-drawer.json 里用 x/y/w/h 字段
function Get-TileCenter($item) {
    return @([int]([double]$item.x + [double]$item.w / 2), [int]([double]$item.y + [double]$item.h / 2))
}

function Close-DrawerIfOpen {
    $handle = [PD3]::ByTitle('PhoneDesktopDrawer')
    if ($handle -ne [IntPtr]::Zero -and [PD3]::IsWindowVisible($handle)) {
        Click-At 200 60
        Start-Sleep -Milliseconds 600
    }
}

function Wait-OverlayUi([int]$timeoutMs = 10000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-Path $overlayUiPath) {
            try {
                $json = Get-Content $overlayUiPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($json.drawerButton -and $json.dock) { return $json }
            } catch { }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

function Wait-DrawerUi([int]$timeoutMs = 8000) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        if (Test-Path $drawerUiPath) {
            try {
                $json = Get-Content $drawerUiPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ($json.window -and $json.items -and $json.total -gt 0 -and @($json.items).Count -gt 0) { return $json }
            } catch { }
        }
        Start-Sleep -Milliseconds 120
    }
    return $null
}

function Read-DrawerUi {
    if (-not (Test-Path $drawerUiPath)) { return $null }
    try { return Get-Content $drawerUiPath -Raw -Encoding UTF8 | ConvertFrom-Json } catch { return $null }
}

function Get-DrawerHandle {
    return [PD3]::ByTitle('PhoneDesktopDrawer')
}

function Open-Drawer($overlayUi) {
    Close-DrawerIfOpen
    Ensure-DesktopVisible | Out-Null
    Remove-Item $drawerUiPath -ErrorAction SilentlyContinue
    $center = Get-Center $overlayUi.drawerButton
    Click-At $center[0] $center[1]
    return (Wait-DrawerUi 8000)
}

# ---------------------------------------------------------------- 准备
Stop-App
Remove-Item $overlayUiPath, $drawerUiPath, $logPath -ErrorAction SilentlyContinue

Section '阶段 3 验收：悬浮按钮与抽屉'
Start-App
$overlayUi = Wait-OverlayUi 12000
if (-not $overlayUi) { Fail '未获取到覆盖层 UI 探针'; throw '阶段 3 验收中止' }
Ensure-DesktopVisible | Out-Null
Start-Sleep -Milliseconds 600

$button = $overlayUi.drawerButton
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$work = [System.Windows.Forms.Screen]::PrimaryScreen.WorkingArea
$dock = $overlayUi.dock

$rightGap = $screen.Width - ($button.X + $button.Width)
$dockTop = $dock.Y
$buttonBottom = $button.Y + $button.Height
if ($rightGap -ge 10 -and $rightGap -le 60) {
    Pass "悬浮按钮位于屏幕右侧（右边距 $([int]$rightGap) px）"
} else {
    Fail "悬浮按钮右边距异常：$([int]$rightGap) px"
}

if ($buttonBottom -lt $dockTop) {
    Pass "悬浮按钮位于 Dock 上方（按钮底 $([int]$buttonBottom)，Dock 顶 $([int]$dockTop)）"
} else {
    Fail "悬浮按钮与 Dock 位置关系异常：$([int]$buttonBottom) vs $([int]$dockTop)"
}

if ([Math]::Abs($button.Width - $button.Height) -le 1 -and $button.Width -ge 40 -and $button.Width -le 64) {
    Pass "悬浮按钮为圆形（$([int]$button.Width)x$([int]$button.Height)）"
} else {
    Fail "悬浮按钮尺寸异常：$($button.Width)x$($button.Height)"
}

$shot = Save-Screenshot '20-desktop-with-button.png'
$shot.Bitmap.Dispose()
Pass "20 桌面 + 悬浮按钮截图：$(Join-Path $shotDir '20-desktop-with-button.png')"

# ---------------------------------------------------------------- 打开抽屉
$drawerUi = Open-Drawer $overlayUi
if (-not $drawerUi) {
    Fail '点击悬浮按钮后抽屉未打开（无 dev-drawer.json）'
    throw '阶段 3 验收中止'
}

$drawerHandle = Get-DrawerHandle
# 首次打开时目录扫描 + 图标预热约 1 秒，等图标铺满再截图
Start-Sleep -Milliseconds 1900
$shot = Save-Screenshot '21-drawer.png'
$shot.Bitmap.Dispose()
Pass "21 抽屉截图：$(Join-Path $shotDir '21-drawer.png')"

$rect = New-Object PD3+RECT
[void][PD3]::GetWindowRect($drawerHandle, [ref]$rect)
$drawerWidth = $rect.R - $rect.L
$drawerHeight = $rect.B - $rect.T
if ($rect.L -eq 0 -and $rect.T -eq 0 -and $drawerWidth -eq $screen.Width -and $drawerHeight -eq $screen.Height) {
    Pass "抽屉为全屏窗口（$drawerWidth x $drawerHeight）"
} else {
    Fail "抽屉不是全屏：$($rect.L),$($rect.T) ${drawerWidth}x${drawerHeight}"
}

if ([PD3]::IsTopMost((Get-DrawerHandle))) {
    Pass '抽屉打开时为 Topmost（浮在普通窗口之上）'
} else {
    Fail '抽屉打开时未置顶'
}

if ([PD3]::IsForeground((Get-DrawerHandle))) {
    Pass '抽屉打开后取得前台窗口（搜索框可直接输入）'
} else {
    Fail '抽屉未取得前台窗口'
}

$total = [int]$drawerUi.total
$filtered = [int]$drawerUi.filtered
if ($total -ge 50) {
    Pass "抽屉内容数量：$total 项（桌面 + 开始菜单混排）"
} else {
    Fail "抽屉内容过少：$total 项"
}

if ($filtered -eq $total) {
    Pass "未搜索时显示全部条目（$filtered / $total）"
} else {
    Fail "未搜索时条目数不一致：$filtered / $total"
}

$names = @($drawerUi.items | ForEach-Object { $_.name })
if ($drawerUi.sorted -eq $true) {
    Pass "条目按名称排序（应用内比较器自检通过；首项 '$($names[0])'，末项 '$($names[$names.Count - 1])'）"
} else {
    Fail "排序自检未通过（sorted=$($drawerUi.sorted)）"
}

$duplicateGroups = @($names | Group-Object | Where-Object { $_.Count -gt 1 })
if ($duplicateGroups.Count -eq 0) {
    Pass "无同名重复项（共 $($names.Count) 个名字全部唯一）"
} else {
    Fail "存在同名重复：$($duplicateGroups[0].Name) x$($duplicateGroups[0].Count)"
}

$desktopItems = @($drawerUi.items | Where-Object { $_.path -like '*\Desktop\*' })
$startMenuItems = @($drawerUi.items | Where-Object { $_.path -like '*Start Menu*' })
if ($desktopItems.Count -gt 0 -and $startMenuItems.Count -gt 0) {
    Pass "内容来源混合：桌面 $($desktopItems.Count) 项、开始菜单 $($startMenuItems.Count) 项"
} else {
    Fail "内容来源缺失：桌面 $($desktopItems.Count)、开始菜单 $($startMenuItems.Count)"
}

$realized = @($drawerUi.items | Where-Object { $_.w -gt 0 -and $_.x -gt 0 })
if ($realized.Count -ge 20) {
    Pass "网格项已布局：$($realized.Count) 个可点击图标"
} else {
    Fail "网格项布局异常：仅 $($realized.Count) 个"
}

# ---------------------------------------------------------------- 搜索
Section '实时搜索'
$searchCenter = Get-Center $drawerUi.search
Click-At $searchCenter[0] $searchCenter[1]
Start-Sleep -Milliseconds 300

if ([PD3]::IsForeground((Get-DrawerHandle))) {
    [System.Windows.Forms.SendKeys]::SendWait('windows')
    Start-Sleep -Milliseconds 900

    $searchUi = Read-DrawerUi
    $searchFiltered = [int]$searchUi.filtered
    if ($searchFiltered -gt 0 -and $searchFiltered -lt $total) {
        Pass "搜索 'windows' 命中 $searchFiltered / $total 项（实时筛选）"
    } else {
        Fail "搜索筛选异常：$searchFiltered / $total"
    }

    $bad = @()
    foreach ($item in $searchUi.items) {
        $haystack = "$($item.name) $($item.path) $($item.target)".ToLowerInvariant()
        if (-not $haystack.Contains('windows')) { $bad += $item.name }
    }
    if ($bad.Count -eq 0) {
        Pass '搜索结果均命中名称/文件名/路径（未出现无关条目）'
    } else {
        Fail "搜索结果包含无关条目：$($bad[0])"
    }

    $shot = Save-Screenshot '22-drawer-search.png'
    $shot.Bitmap.Dispose()
    Pass "22 搜索结果截图：$(Join-Path $shotDir '22-drawer-search.png')"
} else {
    Fail '抽屉未取得前台焦点，跳过键盘搜索测试'
}

# ---------------------------------------------------------------- 双击启动
if (-not $SkipLaunchTest) {
    Section '双击启动'
    if ([PD3]::IsForeground((Get-DrawerHandle))) {
        [System.Windows.Forms.SendKeys]::SendWait('^a')
        Start-Sleep -Milliseconds 120
        [System.Windows.Forms.SendKeys]::SendWait('{DELETE}')
        Start-Sleep -Milliseconds 300
        [System.Windows.Forms.SendKeys]::SendWait('notepad')
        Start-Sleep -Milliseconds 900

        $launchUi = Read-DrawerUi
        $tiles = @($launchUi.items | Where-Object { $_.w -gt 0 -and $_.x -gt 0 -and $_.y -ge 0 })
        if ($tiles.Count -gt 0) {
            $tile = $tiles[0]
            $before = @(Get-Process notepad -ErrorAction SilentlyContinue | Select-Object -ExpandProperty Id)
            $center = Get-TileCenter $tile
            DoubleClick-At $center[0] $center[1]
            Start-Sleep -Milliseconds 1600

            $after = @(Get-Process notepad -ErrorAction SilentlyContinue | Where-Object { $before -notcontains $_.Id })
            if ($after.Count -gt 0) {
                Pass "双击 '$($tile.name)' 启动了应用（新进程 PID $($after[0].Id)）"
                foreach ($process in $after) {
                    $process.CloseMainWindow() | Out-Null
                    Start-Sleep -Milliseconds 200
                    if (-not $process.HasExited) { $process | Stop-Process -Force -ErrorAction SilentlyContinue }
                }
            } else {
                Fail "双击 '$($tile.name)' 后没有新进程"
            }
            if (-not [PD3]::IsWindowVisible((Get-DrawerHandle)) -and -not (Test-Path $drawerUiPath)) {
                Pass '双击启动后抽屉立即关闭'
            } else {
                Fail '双击启动后抽屉未关闭'
                Close-DrawerIfOpen
            }
        } else {
            Fail '搜索结果中没有可点击的图标'
        }
    }
}

# ---------------------------------------------------------------- 关闭方式
Section '关闭方式（Esc / 空白区域）'
Ensure-DesktopVisible | Out-Null
$drawerUi = Open-Drawer $overlayUi
if ($drawerUi) {
    Start-Sleep -Milliseconds 500
    if ([PD3]::IsForeground((Get-DrawerHandle))) {
        [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
        Start-Sleep -Milliseconds 700
        if (-not [PD3]::IsWindowVisible((Get-DrawerHandle))) {
            Pass '按 Esc 关闭抽屉'
        } else {
            Fail '按 Esc 未关闭抽屉'
        }
    } else {
        Fail '抽屉未取得前台焦点，无法测试 Esc'
    }
} else {
    Fail '重新打开抽屉失败'
}

Ensure-DesktopVisible | Out-Null
$drawerUi = Open-Drawer $overlayUi
if ($drawerUi) {
    Start-Sleep -Milliseconds 600
    Click-At 200 60
    Start-Sleep -Milliseconds 900
    if ([PD3]::IsWindowVisible((Get-DrawerHandle))) {
        # 首次点击可能只用于把抽屉切到前台，补一次点击
        Click-At 300 150
        Start-Sleep -Milliseconds 900
    }
    if (-not [PD3]::IsWindowVisible((Get-DrawerHandle))) {
        Pass '点击抽屉空白区域关闭'
    } else {
        Fail '点击空白区域未关闭抽屉'
    }

    if (-not [PD3]::IsTopMost((Get-DrawerHandle))) {
        Pass '关闭后抽屉不再置顶'
    } else {
        Fail '关闭后抽屉仍带 Topmost 样式'
    }
} else {
    Fail '重新打开抽屉失败'
}

# ---------------------------------------------------------------- 置顶覆盖普通窗口
Section '抽屉置顶（普通窗口打开时）'
$drawerUi = Open-Drawer $overlayUi
if ($drawerUi) {
    Start-Sleep -Milliseconds 400
    $notepad = Start-Process notepad -PassThru
    Start-Sleep -Milliseconds 1500
    $notepad.Refresh()
    $noteHwnd = $notepad.MainWindowHandle

    if ($noteHwnd -ne [IntPtr]::Zero) {
        $noteRect = New-Object PD3+RECT
        [void][PD3]::GetWindowRect($noteHwnd, [ref]$noteRect)
        $sampleX = [int](($noteRect.L + $noteRect.R) / 2)
        $sampleY = [int](($noteRect.T + $noteRect.B) / 2)

        $shot = Save-Screenshot '23-drawer-above-window.png'
        $pixel = $shot.Bitmap.GetPixel($sampleX, $sampleY)
        $shot.Bitmap.Dispose()
        Pass "23 抽屉覆盖普通窗口截图：$(Join-Path $shotDir '23-drawer-above-window.png')"

        $isDark = ($pixel.R -le 40 -and $pixel.G -le 40 -and $pixel.B -le 40)
        if ($isDark -and [PD3]::IsTopMost((Get-DrawerHandle))) {
            Pass "普通窗口打开后抽屉仍在其之上（采样像素 R=$($pixel.R) G=$($pixel.G) B=$($pixel.B)）"
        } else {
            Fail "普通窗口盖住了抽屉（采样像素 R=$($pixel.R) G=$($pixel.G) B=$($pixel.B)）"
        }

        if (-not [PD3]::IsWindowVisible((Get-DrawerHandle))) { Fail '抽屉意外关闭' }
    } else {
        Fail '记事本窗口未出现'
    }

    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 400
    [void]$notepad.CloseMainWindow()
    Start-Sleep -Milliseconds 600
    if (-not $notepad.HasExited) { $notepad | Stop-Process -Force -ErrorAction SilentlyContinue }
} else {
    Fail '重新打开抽屉失败'
}

# ---------------------------------------------------------------- 收尾
Ensure-DesktopVisible | Out-Null
$shot = Save-Screenshot '24-desktop-restored.png'
$shot.Bitmap.Dispose()
Pass "24 关闭后桌面截图：$(Join-Path $shotDir '24-desktop-restored.png')"

Stop-App
Start-Sleep -Milliseconds 500
if (-not (Get-Process PhoneDesktop -ErrorAction SilentlyContinue)) {
    Pass '退出后进程结束，无残留'
} else {
    Fail '进程未退出'
    Get-Process PhoneDesktop | Stop-Process -Force
}

Restore-DesktopState

# ---------------------------------------------------------------- 报告
$failCount = ($results | Where-Object { $_ -like 'FAIL*' }).Count
$summary = @()
$summary += '# PhoneDesktop 阶段 3 验收报告'
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
