# 编译 QQAgent 自举启动器（.NET Framework 4.7.2 运行，C# 5 语法）
# 用法：powershell -ExecutionPolicy Bypass -File launcher\build.ps1
param(
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'

$scriptDir = $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptDir
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path $csc)) {
    throw "找不到系统自带的 C# 编译器：$csc"
}

$outDir = Join-Path $scriptDir 'bin'
$outExe = Join-Path $outDir 'QQAgent.Launcher.exe'
if (-not (Test-Path $outDir)) { New-Item -ItemType Directory -Path $outDir | Out-Null }

$source = Join-Path $scriptDir 'Launcher.cs'
$manifest = Join-Path $scriptDir 'app.manifest'
if (-not (Test-Path $source)) { throw "缺少源码：$source" }
if (-not (Test-Path $manifest)) { throw "缺少清单：$manifest" }

Push-Location $repoRoot
try {
    & $csc /nologo /target:winexe /platform:x64 /optimize+ "/out:$outExe" "/win32manifest:$manifest" `
        /reference:System.dll /reference:System.Windows.Forms.dll /reference:System.Drawing.dll /reference:System.Net.Http.dll `
        $source
    if ($LASTEXITCODE -ne 0) {
        throw "csc 编译失败，退出码 $LASTEXITCODE"
    }
}
finally {
    Pop-Location
}

if (-not (Test-Path $outExe)) { throw "编译未产出 $outExe" }
$size = (Get-Item $outExe).Length
Write-Host ("[launcher] 编译完成：{0}" -f $outExe)
Write-Host ("[launcher] 大小：{0:N0} 字节（{1:N1} KB）" -f $size, ($size / 1KB))
if ($size -ge 100KB) {
    Write-Warning ("[launcher] 产物超过 100KB 目标（当前 {0:N1} KB）" -f ($size / 1KB))
}