[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipNotepad,

    [switch]$SkipFullscreen,

    [switch]$SkipClick
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\PhoneDesktop\bin\$Configuration\net8.0-windows\PhoneDesktop.exe"
$shotDir = Join-Path $root 'artifacts\shots\phase1'
$reportPath = Join-Path $root 'artifacts\verify-report.txt'
$logPath = Join-Path $env:APPDATA 'PhoneDesktop\log.txt'

if (-not (Test-Path $exe)) {
    throw "未找到 $exe，请先运行 .\build.ps1"
}

New-Item -ItemType Directory -Force -Path $shotDir | Out-Null
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $reportPath) | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public static class PDV {
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowW")] public static extern IntPtr FindWindow(string c, IntPtr w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowExW")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
  [DllImport("user32.dll")] public static extern IntPtr GetWindowLongPtr(IntPtr h, int idx);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public static IntPtr ChildByTitle(IntPtr parent, string title) { return FindWindowEx(parent, IntPtr.Zero, null, title); }  public static IntPtr Overlay() {
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
  public static List<IntPtr> ChildChain(IntPtr parent) {
    var list = new List<IntPtr>();
    var h = GetWindow(parent, 5);
    var guard = 0;
    while (h != IntPtr.Zero && guard++ < 200) { list.Add(h); h = GetWindow(h, 2); }
    return list;
  }
  public static string Cls(IntPtr h) { var s = new StringBuilder(256); GetClassName(h, s, 256); return s.ToString(); }
  public static bool IsUnder(IntPtr child, IntPtr ancestor) {
    var h = child; var guard = 0;
    while (h != IntPtr.Zero && guard++ < 12) { if (h == ancestor) return true; h = GetParent(h); }
    return false;
  }
  // 桌面层判定：Progman 链上，或 Explorer 临时把桌面子窗口挪到的 WorkerW 下，
  // 或直接命中我们的覆盖层（类名以 HwndWrapper[PhoneDesktop 开头）。
  public static bool IsDesktopSurface(IntPtr hit, IntPtr progman) {
    var h = hit; var guard = 0;
    while (h != IntPtr.Zero && guard++ < 12) {
      if (h == progman) return true;
      var name = Cls(h);
      if (name.StartsWith("HwndWrapper[PhoneDesktop", StringComparison.Ordinal)) return true;
      if (name == "WorkerW") return true;
      h = GetParent(h);
    }
    return false;
  }
}
'@

$script:results = New-Object System.Collections.Generic.List[string]
$script:weToggledDesktop = $false
$script:overlay = [IntPtr]::Zero
$script:progman = [IntPtr]::Zero

function Pass([string]$message) {
    $script:results.Add("PASS  $message") | Out-Null
    Write-Host "  PASS  $message" -ForegroundColor Green
}

function Fail([string]$message) {
    $script:results.Add("FAIL  $message") | Out-Null
    Write-Host "  FAIL  $message" -ForegroundColor Red
}

function Section([string]$title) {
    $script:results.Add("") | Out-Null
    $script:results.Add("## $title") | Out-Null
    Write-Host ""
    Write-Host "== $title ==" -ForegroundColor Cyan
}

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

function Get-ColorDiff($a, $b) {
    return ([Math]::Abs([int]$a.R - [int]$b.R)) + ([Math]::Abs([int]$a.G - [int]$b.G)) + ([Math]::Abs([int]$a.B - [int]$b.B))
}

function Test-TaskbarVisible {
    $taskbar = [PDV]::FindWindow('Shell_TrayWnd', [IntPtr]::Zero)
    return ($taskbar -ne [IntPtr]::Zero -and [PDV]::IsWindowVisible($taskbar))
}

function Test-DesktopVisible {
    $progman = [PDV]::FindWindow('Progman', [IntPtr]::Zero)
    $bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
    $point = New-Object PDV+POINT
    $point.X = [int]($bounds.X + $bounds.Width / 2)
    $point.Y = [int]($bounds.Y + $bounds.Height / 2)
    $hit = [PDV]::WindowFromPoint($point)
    if ($hit -eq [IntPtr]::Zero) { return $false }
    return [PDV]::IsDesktopSurface($hit, $progman)
}

function Ensure-DesktopVisible {
    for ($i = 0; $i -lt 4; $i++) {
        if (Test-DesktopVisible) { return $true }
        $shell = New-Object -ComObject Shell.Application
        $shell.ToggleDesktop()
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        $script:weToggledDesktop = $true
        Start-Sleep -Milliseconds 900
    }
    return (Test-DesktopVisible)
}

function Restore-DesktopState {
    if (-not $script:weToggledDesktop) { return }
    $shell = New-Object -ComObject Shell.Application
    $shell.ToggleDesktop()
    [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
    Start-Sleep -Milliseconds 600
}

function Wait-OverlayOnTop([int]$timeoutMs) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        $hostWindow = [PDV]::GetAncestor($script:overlay, 1)
        if ([PDV]::IsWindowVisible($script:overlay) -and $hostWindow -ne [IntPtr]::Zero -and [PDV]::GetWindow($hostWindow, 5) -eq $script:overlay) {
            return $true
        }
        Start-Sleep -Milliseconds 50
    }
    return $false
}

function Stop-ExistingInstance {
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

function Wait-ForProcessWindow([System.Diagnostics.Process]$process, [int]$timeoutMs) {
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    while ($watch.ElapsedMilliseconds -lt $timeoutMs) {
        if ($process.HasExited) { return $false }
        $process.Refresh()
        if ($process.MainWindowHandle -ne [IntPtr]::Zero) { return $true }
        Start-Sleep -Milliseconds 40
    }
    return $false
}

# ---------------------------------------------------------------- 准备
Stop-ExistingInstance
Remove-Item $logPath -ErrorAction SilentlyContinue
$script:progman = [PDV]::FindWindow('Progman', [IntPtr]::Zero)
if ($script:progman -eq [IntPtr]::Zero) {
    throw '未找到 Progman，桌面环境异常'
}

Section '阶段 1 验收：启动与层级'
$watch = [System.Diagnostics.Stopwatch]::StartNew()
$process = Start-Process -FilePath $exe -ArgumentList '--dev' -PassThru
$script:overlay = [IntPtr]::Zero
while ($watch.ElapsedMilliseconds -lt 8000) {
    $candidate = [PDV]::Overlay()
    if ($candidate -ne [IntPtr]::Zero -and [PDV]::IsWindowVisible($candidate)) {
        $script:overlay = $candidate
        break
    }
    Start-Sleep -Milliseconds 20
}
$watch.Stop()
$startupMs = $watch.ElapsedMilliseconds

if ($script:overlay -ne [IntPtr]::Zero) {
    Pass "覆盖层窗口出现，启动耗时 ${startupMs} ms（冷启动到覆盖层可见）"
} else {
    Fail "8 秒内未检测到覆盖层窗口，启动耗时 ${startupMs} ms"
}

if (-not (Test-TaskbarVisible)) {
    Pass '运行期间已隐藏 Windows 任务栏'
} else {
    Fail '运行期间 Windows 任务栏仍然显示'
}

if ($startupMs -lt 1000) {
    Pass "启动耗时 ${startupMs} ms < 1000 ms"
} else {
    Fail "启动耗时 ${startupMs} ms 超过 1 秒目标"
}

$rect = New-Object PDV+RECT
[void][PDV]::GetWindowRect($script:overlay, [ref]$rect)
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
if ($rect.L -eq $screen.X -and $rect.T -eq $screen.Y -and ($rect.R - $rect.L) -eq $screen.Width -and ($rect.B - $rect.T) -eq $screen.Height) {
    Pass "覆盖层尺寸与主屏一致：$($rect.R - $rect.L)x$($rect.B - $rect.T)"
} else {
    Fail "覆盖层尺寸异常：$($rect.L),$($rect.T) $($rect.R - $rect.L)x$($rect.B - $rect.T)"
}

$overlayParent = [PDV]::GetAncestor($script:overlay, 1)
$overlayParentClass = if ($overlayParent -ne [IntPtr]::Zero) { [PDV]::Cls($overlayParent) } else { '' }
if ($overlayParentClass -eq 'Progman' -or $overlayParentClass -eq 'WorkerW') {
    Pass "覆盖层父窗口是桌面层宿主（$overlayParentClass）"
} else {
    Fail "覆盖层父窗口异常：$overlayParentClass"
}

$exStyle = [PDV]::GetWindowLongPtr($script:overlay, -20).ToInt64()
if (($exStyle -band 0x8) -eq 0) {
    Pass "覆盖层未设置 WS_EX_TOPMOST（exStyle=0x$('{0:X}' -f $exStyle)）"
} else {
    Fail '覆盖层带 WS_EX_TOPMOST，违反“不置顶”约束'
}

$chain = [PDV]::ChildChain($script:progman)
$overlayIndex = -1
$defView = [IntPtr]::Zero
$defIndex = -1
for ($i = 0; $i -lt $chain.Count; $i++) {
    if ($chain[$i] -eq $script:overlay -and $overlayIndex -lt 0) { $overlayIndex = $i }
    if ([PDV]::Cls($chain[$i]) -eq 'SHELLDLL_DefView') { $defView = $chain[$i]; $defIndex = $i }
}
if (Wait-OverlayOnTop 3000) {
    Pass 'z 序正确：覆盖层位于桌面图标层（SHELLDLL_DefView）之上，且窗口可见'
} else {
    Fail "z 序异常：overlayIndex=$overlayIndex defViewIndex=$defIndex，覆盖层未排到图标层之上"
}

if (Ensure-DesktopVisible) {
    Pass '桌面处于可见状态（可进行视觉验证）'
} else {
    Fail '无法让桌面可见'
}

$shot = Save-Screenshot '01-desktop-overlay.png'
$baseline = $shot.Bitmap.GetPixel(1280, 700)
$script:baselineBitmap = $shot.Bitmap
$baselineColor = "R=$($baseline.R) G=$($baseline.G) B=$($baseline.B)"
Pass "01 桌面覆盖层截图：$($shot.Path)（基准像素 $baselineColor）"

# ---------------------------------------------------------------- 点击
if (-not $SkipClick) {
    Section '覆盖层输入验证'
    [void][PDV]::SetCursorPos(640, 300)
    Start-Sleep -Milliseconds 150
    [PDV]::mouse_event(0x02, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 60
    [PDV]::mouse_event(0x04, 0, 0, 0, [UIntPtr]::Zero)
    Start-Sleep -Milliseconds 500

    $logTail = Get-Content $logPath -Encoding UTF8 -ErrorAction SilentlyContinue | Select-Object -Last 30
    if ($logTail -match '覆盖层收到鼠标左键') {
        Pass '覆盖层成功接收 WPF 鼠标左键事件（Dock 交互的基础）'
    } else {
        Fail '覆盖层未收到鼠标左键事件'
    }

    $shot = Save-Screenshot '04-click-overlay.png'
    Pass "04 点击后截图：$($shot.Path)"
    $shot.Bitmap.Dispose()
}

# ---------------------------------------------------------------- 普通窗口
if (-not $SkipNotepad) {
    Section '普通窗口层级验证'
    $notepad = Start-Process notepad -PassThru
    if (Wait-ForProcessWindow $notepad 6000) {
        $notepad.Refresh()
        $npadHwnd = $notepad.MainWindowHandle
        $npadRect = New-Object PDV+RECT
        [void][PDV]::GetWindowRect($npadHwnd, [ref]$npadRect)
        $sampleX = [int](($npadRect.L + $npadRect.R) / 2)
        $sampleY = [int](($npadRect.T + $npadRect.B) / 2)

        $shot = Save-Screenshot '02-notepad-covering.png'
        $covered = $shot.Bitmap.GetPixel($sampleX, $sampleY)
        $shot.Bitmap.Dispose()
        $expectedAtSample = $script:baselineBitmap.GetPixel($sampleX, $sampleY)
        if ((Get-ColorDiff $covered $expectedAtSample) -gt 40) {
            Pass "记事本窗口盖住覆盖层（采样点 $sampleX,$sampleY 像素发生变化）"
        } else {
            Fail '记事本打开后采样点仍是覆盖层内容，覆盖层可能置顶了'
        }
        Pass "02 普通窗口截图：$(Join-Path $shotDir '02-notepad-covering.png')"

        [void][PDV]::ShowWindow($npadHwnd, 6)
        Start-Sleep -Milliseconds 900
        $shot = Save-Screenshot '03-overlay-after-minimize.png'
        $restored = $shot.Bitmap.GetPixel($sampleX, $sampleY)
        $shot.Bitmap.Dispose()
        if ((Get-ColorDiff $restored $expectedAtSample) -le 10) {
            Pass '最小化记事本后覆盖层立即重新露出'
        } else {
            Fail '最小化记事本后覆盖层未恢复显示'
        }
        Pass "03 最小化后截图：$(Join-Path $shotDir '03-overlay-after-minimize.png')"

        [void]$notepad.CloseMainWindow()
        Start-Sleep -Milliseconds 600
        if (-not $notepad.HasExited) {
            Stop-Process -Id $notepad.Id -Force -ErrorAction SilentlyContinue
        }
    } else {
        Fail '记事本未能启动'
        Stop-Process -Id $notepad.Id -Force -ErrorAction SilentlyContinue
    }
}

# ---------------------------------------------------------------- 全屏窗口
if (-not $SkipFullscreen) {
    Section '全屏窗口（模拟游戏）层级验证'
    $fullscreen = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\fullscreen-test.ps1'), '-Seconds', '30' -PassThru
    Start-Sleep -Milliseconds 3000

    $shot = Save-Screenshot '05-fullscreen-covering.png'
    $fullscreenPixel = $shot.Bitmap.GetPixel(1280, 700)
    $shot.Bitmap.Dispose()
    if ((Get-ColorDiff $fullscreenPixel $baseline) -gt 40) {
        Pass '全屏窗口完整盖住覆盖层（中心像素为全屏窗口内容）'
    } else {
        Fail '全屏窗口未能盖住覆盖层'
    }
    Pass "05 全屏窗口截图：$(Join-Path $shotDir '05-fullscreen-covering.png')"

    if ([PDV]::IsWindow($script:overlay)) {
        Pass '全屏窗口出现期间覆盖层窗口仍存活（不主动隐藏、不重排）'
    } else {
        Fail '全屏窗口出现期间覆盖层窗口消失'
    }

    $exStyle = [PDV]::GetWindowLongPtr($script:overlay, -20).ToInt64()
    if (($exStyle -band 0x8) -eq 0 -and [PDV]::GetWindow([PDV]::GetAncestor($script:overlay, 1), 5) -eq $script:overlay) {
        Pass '全屏窗口期间覆盖层未置顶，且仍位于桌面子窗口 z 序最上'
    } else {
        Fail '全屏窗口期间覆盖层层级发生变化'
    }

    Stop-Process -Id $fullscreen.Id -Force -ErrorAction SilentlyContinue
    Start-Sleep -Milliseconds 1200

    $shot = Save-Screenshot '06-after-fullscreen.png'
    $afterFullscreen = $shot.Bitmap.GetPixel(1280, 700)
    $shot.Bitmap.Dispose()
    if ((Get-ColorDiff $afterFullscreen $baseline) -le 10) {
        Pass '关闭全屏窗口后覆盖层恢复显示'
    } else {
        Fail '关闭全屏窗口后覆盖层未恢复'
    }
    Pass "06 全屏关闭后截图：$(Join-Path $shotDir '06-after-fullscreen.png')"
}

# ---------------------------------------------------------------- 性能
Section '空闲资源'
$appProcess = Get-Process PhoneDesktop -ErrorAction SilentlyContinue
if ($appProcess) {
    $cpuBefore = $appProcess.TotalProcessorTime
    Start-Sleep -Seconds 8
    $appProcess.Refresh()
    $cpuAfter = $appProcess.TotalProcessorTime
    $cpuSeconds = ($cpuAfter - $cpuBefore).TotalSeconds
    $cores = [Environment]::ProcessorCount
    $cpuPercent = [Math]::Round(($cpuSeconds / 8 / $cores) * 100, 2)

    $privateMb = [Math]::Round($appProcess.PrivateMemorySize64 / 1MB, 1)
    $workingMb = [Math]::Round($appProcess.WorkingSet64 / 1MB, 1)
    $threads = $appProcess.Threads.Count

    Pass "空闲 8 秒 CPU 占用 ${cpuPercent}%"
    Pass "内存：私有 ${privateMb} MB / 工作集 ${workingMb} MB / 线程 $threads"

    if ($privateMb -lt 100) {
        Pass "私有内存 ${privateMb} MB < 100 MB 目标"
    } else {
        Fail "私有内存 ${privateMb} MB 超过 100 MB 目标"
    }
} else {
    Fail '未找到 PhoneDesktop 进程'
}

# ---------------------------------------------------------------- 退出
Section '退出与桌面恢复'
$exitProcess = Start-Process -FilePath $exe -ArgumentList '--exit' -PassThru
$exitProcess.WaitForExit(5000) | Out-Null
$waited = 0
while ((Get-Process PhoneDesktop -ErrorAction SilentlyContinue) -and $waited -lt 8000) {
    Start-Sleep -Milliseconds 100
    $waited += 100
}

if (-not (Get-Process PhoneDesktop -ErrorAction SilentlyContinue)) {
    Pass '收到退出信号后进程正常结束'
} else {
    Fail '进程未退出'
    Get-Process PhoneDesktop | Stop-Process -Force
}

Start-Sleep -Milliseconds 900
if (Test-TaskbarVisible) {
    Pass '退出后 Windows 任务栏已恢复'
} else {
    Fail '退出后 Windows 任务栏未恢复'
}

if (-not [PDV]::IsWindow($script:overlay)) {
    Pass '覆盖层窗口已销毁'
} else {
    Fail '覆盖层窗口仍然存在'
}

$shot = Save-Screenshot '07-desktop-restored.png'
$restoredPixel = $shot.Bitmap.GetPixel(1280, 700)
$shot.Bitmap.Dispose()
Pass "07 退出后桌面截图：$(Join-Path $shotDir '07-desktop-restored.png')（像素 R=$($restoredPixel.R) G=$($restoredPixel.G) B=$($restoredPixel.B)）"

Restore-DesktopState

# ---------------------------------------------------------------- 报告
$failCount = ($script:results | Where-Object { $_ -like 'FAIL*' }).Count
$summary = @()
$summary += '# PhoneDesktop 阶段 1 验收报告'
$summary += ''
$summary += "时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$summary += "可执行文件：$exe"
$summary += "日志：$logPath"
$summary += "截图目录：$shotDir"
$summary += ''
$summary += $script:results
$summary += ''
if ($failCount -eq 0) {
    $summary += '结论：全部通过'
} else {
    $summary += "结论：$failCount 项失败"
}

$summary -join [Environment]::NewLine | Out-File -FilePath $reportPath -Encoding UTF8
Write-Host ""
Write-Host "报告已写入：$reportPath" -ForegroundColor Cyan

if ($failCount -gt 0) {
    exit 1
}
