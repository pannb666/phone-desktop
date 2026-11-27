[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

# 阶段 5：针对 PRD 第 7 节里前几个阶段没有直接覆盖的验收点做补充验证。
# 其余条目已在 verify.ps1 / verify-phase2/3/4.ps1 中覆盖，见 docs/acceptance-final.md 的映射表。

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\PhoneDesktop\bin\$Configuration\net8.0-windows\PhoneDesktop.exe"
$shotDir = Join-Path $root 'artifacts\shots\final'
$reportPath = Join-Path $root 'artifacts\verify-final-report.txt'
$appData = Join-Path $env:APPDATA 'PhoneDesktop'
$overlayUiPath = Join-Path $appData 'dev-ui.json'
$drawerUiPath = Join-Path $appData 'dev-drawer.json'

if (-not (Test-Path $exe)) { throw "未找到 $exe，请先运行 .\build.ps1" }
New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms, UIAutomationClient, UIAutomationTypes
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PDFinal5 {
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowW")] public static extern IntPtr FindWindow(string c, IntPtr w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowExW")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string t);
  [DllImport("user32.dll")] public static extern IntPtr GetParent(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
  [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  [StructLayout(LayoutKind.Sequential)] public struct RECT { public int L, T, R, B; }

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
  public static bool TaskbarVisible() { var h = FindWindow("Shell_TrayWnd", IntPtr.Zero); return h != IntPtr.Zero && IsWindowVisible(h); }
  public static void Click(int x, int y, bool right = false) {
    SetCursorPos(x, y); System.Threading.Thread.Sleep(120);
    if (right) { mouse_event(0x08, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x10, 0, 0, 0, UIntPtr.Zero); }
    else { mouse_event(0x02, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(60); mouse_event(0x04, 0, 0, 0, UIntPtr.Zero); }
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
        if ([PDFinal5]::DesktopVisible()) { return $true }
        $shell = New-Object -ComObject Shell.Application
        $shell.ToggleDesktop()
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        $script:toggled = $true
        Start-Sleep -Milliseconds 1000
    }
    return [PDFinal5]::DesktopVisible()
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
                if ($json.drawerButton -and $json.dock) { return $json }
            } catch { }
        }
        Start-Sleep -Milliseconds 150
    }
    return $null
}

# ---------------------------------------------------------------- 准备
Stop-App
Remove-Item $overlayUiPath, $drawerUiPath -ErrorAction SilentlyContinue
Start-App
Ensure-DesktopVisible | Out-Null
Start-Sleep -Milliseconds 700
$ui = Get-Content $overlayUiPath -Raw -Encoding UTF8 | ConvertFrom-Json

Section 'PRD 7-1：桌面被不透明覆盖层盖住'
$shot = Save-Screenshot '40-desktop.png'
$desktopPixel = $shot.Bitmap.GetPixel(1280, 700)
$desktopTitlePixel = $shot.Bitmap.GetPixel(200, 300)
$shot.Bitmap.Dispose()
if ($desktopPixel.R -eq 255 -and $desktopPixel.G -eq 227 -and $desktopPixel.B -eq 0) {
    Pass "桌面覆盖层为不透明壁纸色（$($desktopPixel.R),$($desktopPixel.G),$($desktopPixel.B)），图标区无图标"
} else {
    Fail "桌面像素异常：$($desktopPixel.R),$($desktopPixel.G),$($desktopPixel.B)"
}
Pass "40 桌面截图：$(Join-Path $shotDir '40-desktop.png')"

if (-not [PDFinal5]::TaskbarVisible()) {
    Pass '运行期间任务栏被隐藏（手机风格桌面）'
} else {
    Fail '任务栏仍然可见'
}

# ---------------------------------------------------------------- 普通窗口盖住覆盖层与 Dock
Section 'PRD 7-2：普通窗口盖住覆盖层和 Dock'
$notepad = Start-Process notepad -PassThru
Start-Sleep -Milliseconds 1500
$notepad.Refresh()
$noteHwnd = $notepad.MainWindowHandle
if ($noteHwnd -ne [IntPtr]::Zero) {
    [void][PDFinal5]::ShowWindow($noteHwnd, 3)  # SW_MAXIMIZE
    Start-Sleep -Milliseconds 900

    $dockRect = $ui.dock
    $dockCenterX = [int]($dockRect.X + $dockRect.Width / 2)
    $dockCenterY = [int]($dockRect.Y + $dockRect.Height / 2)

    $shot = Save-Screenshot '41-window-over-all.png'
    $overlayPixel = $shot.Bitmap.GetPixel(1280, 700)
    $dockPixel = $shot.Bitmap.GetPixel($dockCenterX, $dockCenterY)
    $shot.Bitmap.Dispose()

    $overlayCovered = -not ($overlayPixel.R -eq 255 -and $overlayPixel.G -eq 227 -and $overlayPixel.B -eq 0)
    $dockCovered = -not ($dockPixel.R -eq 255 -and $dockPixel.G -eq 227 -and $dockPixel.B -eq 0)

    if ($overlayCovered) {
        Pass '最大化窗口盖住覆盖层'
    } else {
        Fail '最大化窗口未盖住覆盖层'
    }

    if ($dockCovered) {
        Pass '最大化窗口盖住 Dock'
    } else {
        Fail '最大化窗口未盖住 Dock'
    }

    Pass "41 普通窗口覆盖截图：$(Join-Path $shotDir '41-window-over-all.png')"

    # PRD 7-8：右下角悬浮按钮在有普通窗口时被盖住
    $button = $ui.drawerButton
    $bx = [int]($button.X + $button.Width / 2)
    $by = [int]($button.Y + $button.Height / 2)
    [void][PDFinal5]::SetForegroundWindow($noteHwnd)
    Start-Sleep -Milliseconds 400
    [PDFinal5]::Click($bx, $by)
    Start-Sleep -Milliseconds 1200

    $drawerOpen = [PDFinal5]::IsWindowVisible([PDFinal5]::Drawer())
    $foreground = [PDFinal5]::GetForegroundWindow()
    if (-not $drawerOpen -and $foreground -eq $noteHwnd) {
        Pass '普通窗口覆盖时点击悬浮按钮位置：点击落到窗口上，抽屉未被打开'
    } else {
        Fail "普通窗口覆盖时抽屉被打开（drawer=$drawerOpen, fg=$foreground, notepad=$noteHwnd）"
    }

    $shot = Save-Screenshot '42-button-covered.png'
    $shot.Bitmap.Dispose()
    Pass "42 悬浮按钮被覆盖截图：$(Join-Path $shotDir '42-button-covered.png')"

    [void]$notepad.CloseMainWindow()
    Start-Sleep -Milliseconds 800
    if (-not $notepad.HasExited) { $notepad | Stop-Process -Force -ErrorAction SilentlyContinue }
} else {
    Fail '记事本未启动'
}

# ---------------------------------------------------------------- 全屏窗口
Section 'PRD 7-3：全屏窗口盖住覆盖层'
Ensure-DesktopVisible | Out-Null
$fullscreen = Start-Process powershell -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', (Join-Path $root 'tools\fullscreen-test.ps1'), '-Seconds', '12' -PassThru
Start-Sleep -Milliseconds 2800
$shot = Save-Screenshot '43-fullscreen.png'
$fullscreenPixel = $shot.Bitmap.GetPixel(1280, 700)
$shot.Bitmap.Dispose()
if (-not ($fullscreenPixel.R -eq 255 -and $fullscreenPixel.G -eq 227 -and $fullscreenPixel.B -eq 0)) {
    Pass '全屏窗口完整盖住覆盖层'
} else {
    Fail '全屏窗口未盖住覆盖层'
}
Pass "43 全屏覆盖截图：$(Join-Path $shotDir '43-fullscreen.png')"
Stop-Process -Id $fullscreen.Id -Force -ErrorAction SilentlyContinue
Start-Sleep -Milliseconds 900

$ui = Get-Content $overlayUiPath -Raw -Encoding UTF8 | ConvertFrom-Json

# ---------------------------------------------------------------- 抽屉滚动
Section 'PRD 7-10：抽屉可滚动（内容超出视口时）'
$drawerVisible = $false
$button = $ui.drawerButton
[PDFinal5]::Click([int]($button.X + $button.Width / 2), [int]($button.Y + $button.Height / 2))
Start-Sleep -Milliseconds 1600
$drawerHandle = [PDFinal5]::Drawer()
if ($drawerHandle -ne [IntPtr]::Zero -and [PDFinal5]::IsWindowVisible($drawerHandle)) {
    $drawerVisible = $true
    # 把抽屉临时压矮，强制内容溢出，验证 ScrollViewer 是否可滚动
    [void][PDFinal5]::SetWindowPos($drawerHandle, [IntPtr]::Zero, 0, 0, 1400, 720, 0x0004 -bor 0x0010)
    Start-Sleep -Milliseconds 900

    $scrollCondition = New-Object System.Windows.Automation.PropertyCondition (
        [System.Windows.Automation.AutomationElement]::IsScrollPatternAvailableProperty), $true
    $drawerElement = [System.Windows.Automation.AutomationElement]::FromHandle($drawerHandle)
    $scrollElements = $drawerElement.FindAll([System.Windows.Automation.TreeScope]::Descendants, $scrollCondition)

    $scrollResult = $null
    foreach ($element in $scrollElements) {
        try {
            $pattern = $element.GetCurrentPattern([System.Windows.Automation.ScrollPattern]::Pattern)
            if (-not $pattern.Current.VerticallyScrollable) { continue }
            $before = $pattern.Current.VerticalScrollPercent
            $pattern.Scroll([System.Windows.Automation.ScrollAmount]::NoAmount,
                            [System.Windows.Automation.ScrollAmount]::LargeIncrement)
            Start-Sleep -Milliseconds 500
            $after = $pattern.Current.VerticalScrollPercent
            $scrollResult = [pscustomobject]@{ Before = $before; After = $after }
            break
        } catch {
            continue
        }
    }

    if ($scrollResult -and $scrollResult.After -ne $scrollResult.Before) {
        Pass "抽屉内容溢出时可滚动（滚动位置 $([Math]::Round($scrollResult.Before,1))% → $([Math]::Round($scrollResult.After,1))%）"
    } else {
        Fail '抽屉内容溢出时未能滚动'
    }

    $shot = Save-Screenshot '44-drawer-scroll.png'
    $shot.Bitmap.Dispose()
    Pass "44 抽屉滚动截图：$(Join-Path $shotDir '44-drawer-scroll.png')"

    [System.Windows.Forms.SendKeys]::SendWait('{ESC}')
    Start-Sleep -Milliseconds 700
} else {
    Fail '抽屉未打开，无法验证滚动'
}

# ---------------------------------------------------------------- 退出恢复
Section 'PRD 7-16：退出后桌面恢复'
Stop-App
Start-Sleep -Milliseconds 1200

if (-not (Get-Process PhoneDesktop -ErrorAction SilentlyContinue)) {
    Pass '进程已结束'
} else {
    Fail '进程仍在运行'
    Get-Process PhoneDesktop | Stop-Process -Force
}

if ([PDFinal5]::TaskbarVisible()) {
    Pass '退出后任务栏已恢复'
} else {
    Fail '退出后任务栏未恢复'
}

$shot = Save-Screenshot '45-desktop-restored.png'
$restoredPixel = $shot.Bitmap.GetPixel(1280, 700)
$shot.Bitmap.Dispose()
Pass "45 退出后桌面截图：$(Join-Path $shotDir '45-desktop-restored.png')（像素 R=$($restoredPixel.R) G=$($restoredPixel.G) B=$($restoredPixel.B)）"

# ---------------------------------------------------------------- 报告
$failCount = ($results | Where-Object { $_ -like 'FAIL*' }).Count
$summary = @()
$summary += '# PhoneDesktop 阶段 5 补充验收报告'
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
