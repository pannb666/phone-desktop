[CmdletBinding()]
param(
    [ValidateSet('build', 'run', 'run-dev', 'stop', 'publish', 'clean')]
    [string]$Action = 'build',

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SelfContained
)

$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$dotnet = Join-Path $root '.tools\dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) {
    $command = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $command) {
        throw '未找到 dotnet。请安装 .NET 8 SDK，或把 SDK 解压到 .tools\dotnet。'
    }

    $dotnet = $command.Source
}

$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'

$project = Join-Path $root 'src\PhoneDesktop\PhoneDesktop.csproj'
$outputDir = Join-Path $root "src\PhoneDesktop\bin\$Configuration\net8.0-windows"
$exe = Join-Path $outputDir 'PhoneDesktop.exe'

function Invoke-Build {
    Write-Host "==> 编译 $Configuration" -ForegroundColor Cyan
    & $dotnet build $project -c $Configuration --nologo
    if ($LASTEXITCODE -ne 0) {
        throw "编译失败（exit $LASTEXITCODE）"
    }
}

switch ($Action) {
    'build' {
        Invoke-Build
    }

    'run' {
        Invoke-Build
        Start-Process -FilePath $exe -WorkingDirectory $outputDir | Out-Null
        Write-Host "已启动 PhoneDesktop（无调试参数）。" -ForegroundColor Green
    }

    'run-dev' {
        Invoke-Build
        $process = Start-Process -FilePath $exe -ArgumentList '--dev' -WorkingDirectory $outputDir -PassThru
        Write-Host "已启动 PhoneDesktop --dev（PID $($process.Id)，Ctrl+Alt+Q 退出）。" -ForegroundColor Green
    }

    'stop' {
        if (-not (Test-Path $exe)) {
            throw "尚未编译：$exe"
        }

        & $exe --exit
        Start-Sleep -Milliseconds 400
        Write-Host '已发送退出信号。' -ForegroundColor Green
    }

    'publish' {
        Invoke-Build
        $publishDir = Join-Path $root 'dist\PhoneDesktop'
        $selfContainedValue = if ($SelfContained) { 'true' } else { 'false' }
        & $dotnet publish $project -c $Configuration -r win-x64 --self-contained $selfContainedValue -o $publishDir --nologo
        if ($LASTEXITCODE -ne 0) {
            throw "发布失败（exit $LASTEXITCODE）"
        }

        Write-Host "发布产物：$publishDir" -ForegroundColor Green
    }

    'clean' {
        & $dotnet clean $project -c $Configuration --nologo
    }
}
