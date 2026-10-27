#Requires -Version 7
<#
.SYNOPSIS
    检查环境并启动 QQ-AI_Agent_bot（OneBot11 正向 WS + OpenAI 兼容接口）。
.EXAMPLE
    pwsh -NoProfile -File .\scripts\start-bot.ps1 -DryRun
#>
[CmdletBinding()]
param(
    [switch]$DryRun
)

$ErrorActionPreference = 'Continue'
# 统一控制台输出为 UTF-8，避免中文日志乱码
try {
    [Console]::OutputEncoding = [System.Text.Encoding]::UTF8
    $OutputEncoding = [System.Text.Encoding]::UTF8
} catch { }
$BotRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $BotRoot

function Write-Ok([string]$Text) {
    Write-Host "[ OK ] $Text" -ForegroundColor Green
}

function Write-Fail([string]$Text) {
    Write-Host "[FAIL] $Text" -ForegroundColor Red
}

# 1. python 可执行
$pythonCmd = Get-Command python -ErrorAction SilentlyContinue
if (-not $pythonCmd) {
    Write-Fail '未找到 python，请安装 Python 3.10+ 并加入 PATH'
    exit 1
}
$pyVersion = (& python --version 2>&1) -join ' '
if ($LASTEXITCODE -ne 0) {
    Write-Fail "python 不可用: $pyVersion"
    exit 1
}
Write-Ok "python 可用: $pyVersion"

# 2. 配置文件
$configFile = Join-Path $BotRoot 'config\config.yaml'
if (-not (Test-Path -LiteralPath $configFile)) {
    Write-Fail "缺少 config\config.yaml，请执行: Copy-Item config\config.example.yaml config\config.yaml"
    exit 1
}
Write-Ok 'config\config.yaml 存在'

# 3. 密钥文件
$envFile = Join-Path $BotRoot '.env'
if (-not (Test-Path -LiteralPath $envFile)) {
    Write-Fail '缺少 .env，请执行: Copy-Item .env.example .env 并填写 LLM_API_KEY'
    exit 1
}
Write-Ok '.env 存在'

# 4. 依赖可导入
$depOut = (& python -c "import aiohttp,dotenv,yaml" 2>&1) -join ' '
if ($LASTEXITCODE -ne 0) {
    Write-Fail "依赖缺失: $depOut"
    Write-Fail '请执行: pip install -r requirements.txt'
    exit 1
}
Write-Ok '依赖可导入: aiohttp / python-dotenv / PyYAML'

if ($DryRun) {
    Write-Host ''
    Write-Host 'DryRun：检查完成，未启动 bot。' -ForegroundColor Cyan
    exit 0
}

Write-Host ''
Write-Host "启动 bot（工作目录 $BotRoot）..." -ForegroundColor Cyan
& python -m src.main
exit $LASTEXITCODE
