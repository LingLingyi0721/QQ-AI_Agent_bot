#Requires -Version 7
param([switch]$DryRun, [string]$QQRoot)

[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$BotRoot   = Split-Path -Parent $PSScriptRoot
$NapCatDir = Join-Path $BotRoot 'runtime\napcat'

function Fail([string]$Msg) {
    Write-Host "[FAIL] $Msg" -ForegroundColor Red
    exit 1
}

function Info([string]$Msg) {
    Write-Host "[ OK ] $Msg" -ForegroundColor Green
}

# ---------- QQ 根目录来源：参数 > qq-root.txt ----------
if (-not $QQRoot) {
    $cfgPath = Join-Path $BotRoot 'qq-root.txt'
    if (-not (Test-Path -LiteralPath $cfgPath)) {
        Fail "未指定 -QQRoot，且配置文件不存在：$cfgPath（请创建该文件并写入一行 QQ 根目录路径）"
    }
    $firstLine = Get-Content -LiteralPath $cfgPath -Encoding utf8 | Where-Object { $_.Trim() } | Select-Object -First 1
    if (-not $firstLine) {
        Fail "配置文件为空：$cfgPath（请写入一行 QQ 根目录路径）"
    }
    $QQRoot = $firstLine.Trim()
}

if (-not (Test-Path -LiteralPath $QQRoot)) {
    Fail "QQ 根目录不存在：$QQRoot"
}
$QQRoot = (Resolve-Path -LiteralPath $QQRoot).Path

# ---------- 版本探测（失败即退出，不做 fallback）----------
$verCfgPath = Join-Path $QQRoot 'versions\config.json'
if (-not (Test-Path -LiteralPath $verCfgPath)) {
    Fail "缺少版本配置：$verCfgPath"
}
$verCfg     = Get-Content -LiteralPath $verCfgPath -Raw -Encoding utf8 | ConvertFrom-Json
$curVersion = $verCfg.curVersion
$buildId    = $verCfg.buildId
if (-not $curVersion) { Fail "versions\config.json 缺少 curVersion" }
if (-not $buildId)    { Fail "versions\config.json 缺少 buildId" }

$verDir  = Join-Path $QQRoot "versions\$curVersion"
$appDir  = Join-Path $verDir 'resources\app'
$pkgPath = Join-Path $appDir 'package.json'
if (-not (Test-Path -LiteralPath $pkgPath)) {
    Fail "缺少 package.json：$pkgPath"
}
$pkg = Get-Content -LiteralPath $pkgPath -Raw -Encoding utf8 | ConvertFrom-Json

if ("$($pkg.version)" -ne "$curVersion") {
    Write-Host "[FAIL] 版本不一致：versions\config.json curVersion = '$curVersion'，package.json version = '$($pkg.version)'" -ForegroundColor Red
    exit 1
}
if ("$($pkg.buildVersion)" -ne "$buildId") {
    Write-Host "[FAIL] build 不一致：versions\config.json buildId = '$buildId'，package.json buildVersion = '$($pkg.buildVersion)'" -ForegroundColor Red
    exit 1
}

# 非安装版结构：版本目录内有 resources\app\application.asar，无 QQ.exe
$asarPath = Join-Path $appDir 'application.asar'
if (-not (Test-Path -LiteralPath $asarPath)) {
    Fail "缺少 application.asar：$asarPath"
}

$QQExe = Join-Path $QQRoot 'QQ.exe'
if (-not (Test-Path -LiteralPath $QQExe)) {
    Fail "缺少启动器：$QQExe"
}

# ---------- NapCat 环境变量 ----------
$napCatEnv = [ordered]@{
    NAPCAT_LAUNCHER_PATH = Join-Path $NapCatDir 'extracted\NapCatWinBootMain.exe'
    NAPCAT_INJECT_PATH   = Join-Path $NapCatDir 'extracted\NapCatWinBootHook.dll'
    NAPCAT_PATCH_PACKAGE = Join-Path $NapCatDir 'extracted\loadNapCat.js'
    NAPCAT_LOAD_PATH     = Join-Path $NapCatDir 'extracted\napcat.mjs'
    NAPCAT_QQNT_JSON     = Join-Path $NapCatDir 'extracted\qqnt.json'
}

foreach ($k in $napCatEnv.Keys) {
    $p = $napCatEnv[$k]
    if (-not (Test-Path -LiteralPath $p)) {
        Fail "NapCat 文件缺失（$k）：$p"
    }
    Set-Item -Path "Env:$k" -Value $p
}

# ---------- 探测结果 ----------
Write-Host '--- 探测结果 ---'
Write-Host "QQ 根目录   : $QQRoot"
Write-Host "版本        : $curVersion"
Write-Host "build       : $buildId"
Write-Host "版本目录    : $verDir"
Write-Host "NapCat 目录 : $NapCatDir"
Info '校验通过'

if ($DryRun) {
    Write-Host '[DryRun] 仅校验，未启动 QQ。'
    exit 0
}

# ---------- 启动（需管理员）----------
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator
)
if (-not $isAdmin) {
    Write-Host '[FAIL] 需要管理员权限，请以管理员身份运行 PowerShell 后重试。' -ForegroundColor Red
    exit 1
}

$QQPath = $QQExe
Write-Host "启动中：$QQPath"
& $env:NAPCAT_LAUNCHER_PATH $QQPath $env:NAPCAT_INJECT_PATH

Write-Host 'QQ 进程已退出，NapCat 注入会话结束。'
