[CmdletBinding()]
param(
    [int]$Seconds = 20
)

# 模拟"全屏游戏/全屏视频"：一个覆盖整屏（含任务栏）的普通顶层窗口，且不置顶。
Add-Type -AssemblyName System.Windows.Forms, System.Drawing

$form = New-Object System.Windows.Forms.Form
$form.Text = 'PhoneDesktopFullscreenTest'
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::None
$form.StartPosition = [System.Windows.Forms.FormStartPosition]::Manual
$form.Bounds = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$form.BackColor = [System.Drawing.Color]::FromArgb(180, 30, 200)
$form.ShowInTaskbar = $false
$form.TopMost = $false

$label = New-Object System.Windows.Forms.Label
$label.Text = '全屏窗口测试（应完整盖住 PhoneDesktop 覆盖层）'
$label.ForeColor = [System.Drawing.Color]::White
$label.Font = New-Object System.Drawing.Font('Microsoft YaHei UI', 28)
$label.AutoSize = $false
$label.Dock = [System.Windows.Forms.DockStyle]::Fill
$label.TextAlign = [System.Drawing.ContentAlignment]::MiddleCenter
$form.Controls.Add($label)

$timer = New-Object System.Windows.Forms.Timer
$timer.Interval = [Math]::Max(1000, $Seconds * 1000)
$timer.Add_Tick({
    $timer.Stop()
    $form.Close()
})
$timer.Start()

[void][System.Windows.Forms.Application]::Run($form)
