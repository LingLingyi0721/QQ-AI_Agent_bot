# 一键打包 QQAgent 桌面端（WinUI 3 主程序 + 自举启动器）
# 用法：pwsh -File scripts\build-gui.ps1 [-Configuration Release] [-OutDir <目录>]
# 默认输出到仓库根：<根>\QQAgent.exe + <根>\QQAgent.App\
param(
    [string]$Configuration = 'Release',
    [string]$OutDir = ''
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$repoRoot  = Split-Path -Parent $scriptDir

if ([string]::IsNullOrWhiteSpace($OutDir)) {
    $full = $repoRoot
}
elseif ([IO.Path]::IsPathRooted($OutDir)) {
    $full = $OutDir
}
else {
    $full = Join-Path $repoRoot $OutDir
}
$full = [IO.Path]::GetFullPath($full)
if ($full -match '^[A-Za-z]:\\?$') { throw "拒绝输出到盘根：$full" }

$proj = Join-Path $repoRoot 'app\QQAgent.WinUI\QQAgent.WinUI.csproj'
$launcherScript = Join-Path $repoRoot 'launcher\build.ps1'
$launcherExe = Join-Path $repoRoot 'launcher\bin\QQAgent.Launcher.exe'

$targetExe = Join-Path $full 'QQAgent.exe'
$targetApp = Join-Path $full 'QQAgent.App'

Write-Host "[1/6] 构建主程序：$proj"
& dotnet build $proj -c $Configuration -r win-x64 --nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet build 失败，退出码 $LASTEXITCODE" }
Write-Host "[2/6] 编译自举启动器"
& $launcherScript -Configuration $Configuration
if (-not (Test-Path $launcherExe)) { throw "启动器未产出：$launcherExe" }

$appExe = Get-ChildItem -Path (Join-Path $repoRoot 'app\QQAgent.WinUI\bin') -Recurse -Filter 'QQAgent.WinUI.exe' -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\$Configuration\*" } |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1
if (-not $appExe) { throw "找不到 QQAgent.WinUI.exe 产物，请先确认 dotnet build 成功" }
$appOutDir = $appExe.Directory.FullName
Write-Host "[3/6] 主程序产物：$appOutDir"

Write-Host "[4/6] 输出目录：$full"
if (-not (Test-Path -LiteralPath $full)) {
    New-Item -ItemType Directory -Path $full -Force | Out-Null
}
# 只清理旧的 QQAgent.exe 与 QQAgent.App，不动目录内其他文件
if (Test-Path -LiteralPath $targetExe) { Remove-Item -LiteralPath $targetExe -Force }
if (Test-Path -LiteralPath $targetApp) { Remove-Item -LiteralPath $targetApp -Recurse -Force }
New-Item -ItemType Directory -Path $targetApp -Force | Out-Null

Write-Host "[5/6] 复制产物：QQAgent.App\ + QQAgent.exe"
Copy-Item -Path (Join-Path $appOutDir '*') -Destination $targetApp -Recurse -Force
Copy-Item -LiteralPath $launcherExe -Destination $targetExe -Force

$readme = 'QQAgent 桌面端（WinUI 3）
=========================

启动：双击 QQAgent.exe

首次运行会自动检查并安装缺失的运行环境（需要管理员权限，会自动请求 UAC）：
  - .NET 桌面运行时 10.x
  - Windows App Runtime 1.8
安装期间会显示进度窗，请勿关闭。安装完成后启动器会自动拉起主程序。

如自动安装失败，可手动下载安装：
  - .NET 桌面运行时 10.0：https://dotnet.microsoft.com/download/dotnet/10.0
  - Windows App Runtime 1.8：https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads

命令行参数：
  QQAgent.exe --check       只检测不安装，结果写入 %TEMP%\qqagent-launcher-check.txt
  QQAgent.exe --no-install  检测到缺失时只提示，不自动安装

目录结构：
  QQAgent.exe     自举启动器（负责检测/安装运行时并启动主程序）
  QQAgent.App\    主程序 QQAgent.WinUI.exe 及其依赖，请勿移动或改名

系统要求：Windows 10 1809 及以上 / Windows 11，x64
未签名分发时首次运行可能被 SmartScreen 拦截，选择“仍要运行”即可。'
[IO.File]::WriteAllText((Join-Path $targetApp 'README.txt'), $readme, (New-Object System.Text.UTF8Encoding($true)))
Write-Host "[6/6] 已写入 QQAgent.App\README.txt"

$sum = (($(Get-ChildItem -LiteralPath $targetApp -Recurse -File) | Measure-Object -Property Length -Sum).Sum)
$rel = if ($full -eq $repoRoot) { '.' } else { $full }
Write-Host ("[dist] 输出目录：{0}" -f $rel)
Write-Host ("[dist] QQAgent.exe：{0:N0} 字节" -f (Get-Item -LiteralPath $targetExe).Length)
Write-Host ("[dist] QQAgent.App 文件数：{0}，总大小：{1:N0} 字节（{2:N1} MB）" -f ((Get-ChildItem -LiteralPath $targetApp -Recurse -File).Count), $sum, ($sum / 1MB))