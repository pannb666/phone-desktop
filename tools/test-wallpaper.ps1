[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release'
)

# 临时把桌面壁纸换成一张测试图，验证“读取并绘制当前壁纸”的主路径；结束后恢复原壁纸。
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "src\PhoneDesktop\bin\$Configuration\net8.0-windows\PhoneDesktop.exe"
$shotDir = Join-Path $root 'artifacts\shots\phase1'
$reportPath = Join-Path $root 'artifacts\wallpaper-report.txt'
$testImage = Join-Path $env:TEMP 'PhoneDesktop-wallpaper-test.jpg'

New-Item -ItemType Directory -Force -Path $shotDir | Out-Null

Add-Type -AssemblyName System.Drawing, System.Windows.Forms
Add-Type -TypeDefinition @'
using System;
using System.Text;
using System.Runtime.InteropServices;
public static class PDI {
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowW")] public static extern IntPtr FindWindow(string c, IntPtr w);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, EntryPoint="FindWindowExW")] public static extern IntPtr FindWindowEx(IntPtr p, IntPtr a, string c, string w);
  [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
  [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
  [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
  [DllImport("user32.dll")] public static extern int GetSystemMetrics(int index);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool SystemParametersInfo(uint action, uint param, string value, uint winIni);
  [DllImport("user32.dll", CharSet=CharSet.Unicode, SetLastError=true)] public static extern bool SystemParametersInfo(uint action, uint param, StringBuilder value, uint winIni);
  [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
  public static IntPtr ChildByTitle(IntPtr p, string t) { return FindWindowEx(p, IntPtr.Zero, null, t); }
  public static string GetWallpaperPath() {
    var sb = new StringBuilder(260);
    return SystemParametersInfo(0x0073, (uint)sb.Capacity, sb, 0) ? sb.ToString() : string.Empty;
  }
  public static bool SetWallpaper(string path) {
    return SystemParametersInfo(0x0014, 0, path, 0x01 | 0x02);
  }
  public static bool IsDesktopVisible() {
    var progman = FindWindow("Progman", IntPtr.Zero);
    var p = new POINT(); p.X = GetSystemMetrics(0) / 2; p.Y = GetSystemMetrics(1) / 2;
    var hit = WindowFromPoint(p);
    if (hit == IntPtr.Zero) return false;
    return GetAncestor(hit, 2) == progman;
  }
}
'@

$results = New-Object System.Collections.Generic.List[string]
function Pass([string]$m) { $script:results.Add("PASS  $m") | Out-Null; Write-Host "  PASS  $m" -ForegroundColor Green }
function Fail([string]$m) { $script:results.Add("FAIL  $m") | Out-Null; Write-Host "  FAIL  $m" -ForegroundColor Red }

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
        if ([PDI]::IsDesktopVisible()) { return $true }
        $shell = New-Object -ComObject Shell.Application
        $shell.ToggleDesktop()
        [void][System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell)
        Start-Sleep -Milliseconds 900
    }
    return [PDI]::IsDesktopVisible()
}

function Stop-App {
    $process = Get-Process PhoneDesktop -ErrorAction SilentlyContinue
    if ($process) {
        & $exe --exit 2>$null | Out-Null
        Start-Sleep -Milliseconds 900
        if (Get-Process PhoneDesktop -ErrorAction SilentlyContinue) {
            Get-Process PhoneDesktop | Stop-Process -Force
            Start-Sleep -Milliseconds 400
        }
    }
}

# ---------------------------------------------------------------- 准备测试壁纸
$original = [PDI]::GetWallpaperPath()
Write-Host "原始壁纸：'$original'" -ForegroundColor DarkGray

$bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$image = New-Object System.Drawing.Bitmap($bounds.Width, $bounds.Height)
$graphics = [System.Drawing.Graphics]::FromImage($image)
$rect = New-Object System.Drawing.Rectangle(0, 0, $bounds.Width, $bounds.Height)
$brush = New-Object System.Drawing.Drawing2D.LinearGradientBrush($rect, [System.Drawing.Color]::FromArgb(18, 52, 150), [System.Drawing.Color]::FromArgb(150, 40, 160), 30.0)
$graphics.FillRectangle($brush, $rect)
$font = New-Object System.Drawing.Font('Microsoft YaHei UI', 96, [System.Drawing.FontStyle]::Bold)
$format = New-Object System.Drawing.StringFormat
$format.Alignment = [System.Drawing.StringAlignment]::Center
$graphics.DrawString('壁纸绘制测试', $font, [System.Drawing.Brushes]::White, (New-Object System.Drawing.RectangleF(0, 600, $bounds.Width, 300)), $format)
$graphics.Dispose()
$image.Save($testImage, [System.Drawing.Imaging.ImageFormat]::Jpeg)
$image.Dispose()
$brush.Dispose()

if (-not [PDI]::SetWallpaper($testImage)) {
    throw '设置测试壁纸失败'
}
Start-Sleep -Seconds 2

$appliedPath = [PDI]::GetWallpaperPath()
Write-Host "已切换壁纸：'$appliedPath'" -ForegroundColor DarkGray
if ($appliedPath) { Pass "测试壁纸已生效：$appliedPath" } else { Fail '测试壁纸未生效' }

try {
    # ------------------------------------------------------------ 启动并验证
    Stop-App
    Remove-Item (Join-Path $env:APPDATA 'PhoneDesktop\log.txt') -ErrorAction SilentlyContinue
    $process = Start-Process -FilePath $exe -ArgumentList '--dev' -PassThru
    Start-Sleep -Milliseconds 1500

    if (Ensure-DesktopVisible) { Pass '桌面已显示，可截取覆盖层' } else { Fail '无法让桌面可见' }

    $shot = Save-Screenshot '08-wallpaper-overlay.png'
    $left = $shot.Bitmap.GetPixel(200, 300)
    $right = $shot.Bitmap.GetPixel(2300, 1100)
    $brightHits = 0
    for ($y = 690; $y -le 790; $y += 10) {
        for ($x = 1300; $x -le 1660; $x += 10) {
            $pixel = $shot.Bitmap.GetPixel($x, $y)
            if ($pixel.R -gt 200 -and $pixel.G -gt 200 -and $pixel.B -gt 200) { $brightHits++ }
        }
    }
    $shot.Bitmap.Dispose()
    Pass "08 壁纸覆盖层截图：$(Join-Path $shotDir '08-wallpaper-overlay.png')"

    $leftBlue = ($left.B -gt 90) -and ($left.R -lt 90)
    $rightPurple = ($right.R -gt 90) -and ($right.B -gt 90) -and ($right.G -lt 110)
    if ($leftBlue -and $rightPurple) {
        Pass "覆盖层绘制了壁纸渐变（左 R=$($left.R) G=$($left.G) B=$($left.B)；右 R=$($right.R) G=$($right.G) B=$($right.B)）"
    } else {
        Fail "覆盖层未绘制预期的壁纸渐变（左 $($left.R),$($left.G),$($left.B)；右 $($right.R),$($right.G),$($right.B)）"
    }

    if ($brightHits -gt 10) {
        Pass "壁纸中的白色文字被正确绘制（命中 $brightHits 个白色采样点，说明读到了完整图片而非纯色）"
    } else {
        Fail "未在壁纸文字区域采样到白色像素（命中 $brightHits）"
    }

    $logTail = Get-Content (Join-Path $env:APPDATA 'PhoneDesktop\log.txt') -Encoding UTF8 -ErrorAction SilentlyContinue
    if ($logTail -match [regex]::Escape('PhoneDesktop-wallpaper-test.jpg')) {
        Pass '日志确认壁纸来源为当前桌面壁纸文件'
    } else {
        Fail '日志未记录壁纸文件来源'
    }

    Stop-App
}
finally {
    # ------------------------------------------------------------ 恢复
    [void][PDI]::SetWallpaper($original)
    Start-Sleep -Seconds 2
    Remove-Item $testImage -ErrorAction SilentlyContinue

    $restored = [PDI]::GetWallpaperPath()
    if ($restored -eq $original) {
        Pass "已恢复原始壁纸（'$restored'）"
    } else {
        Fail "壁纸恢复失败：'$restored'"
    }

    $shot = Save-Screenshot '09-desktop-restored.png'
    $pixel = $shot.Bitmap.GetPixel(1280, 700)
    $shot.Bitmap.Dispose()
    Pass "09 恢复后桌面截图：$(Join-Path $shotDir '09-desktop-restored.png')（像素 R=$($pixel.R) G=$($pixel.G) B=$($pixel.B)）"
}

$failCount = ($results | Where-Object { $_ -like 'FAIL*' }).Count
$summary = @()
$summary += '# PhoneDesktop 阶段 1 壁纸路径验证'
$summary += ''
$summary += "时间：$(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
$summary += "原始壁纸：'$original'"
$summary += ''
$summary += $results
$summary += ''
if ($failCount -eq 0) { $summary += '结论：全部通过' } else { $summary += "结论：$failCount 项失败" }
$summary -join [Environment]::NewLine | Out-File -FilePath $reportPath -Encoding UTF8
Write-Host ""
Write-Host "报告已写入：$reportPath" -ForegroundColor Cyan

if ($failCount -gt 0) { exit 1 }
