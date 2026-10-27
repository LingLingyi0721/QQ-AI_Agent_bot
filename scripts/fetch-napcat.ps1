#Requires -Version 7
param([string]$Version='v4.18.38',[switch]$Latest,[switch]$Force)
[Console]::OutputEncoding = [System.Text.Encoding]::UTF8
$ErrorActionPreference = 'Stop'

$BotRoot   = Split-Path -Parent $PSScriptRoot
$Runtime   = Join-Path $BotRoot 'runtime\napcat'
$Zip       = Join-Path $Runtime 'NapCat.Shell.zip'
$Extracted = Join-Path $Runtime 'extracted'

$Repo      = 'NapNeko/NapCatQQ'
$AssetName = 'NapCat.Shell.zip'
$Incoming  = Join-Path $Runtime '_incoming'
$RequiredFiles = @('NapCatWinBootMain.exe','NapCatWinBootHook.dll','loadNapCat.js','napcat.mjs','qqnt.json')

$BackupPath = $null

function Fail([string]$Msg) {
    Write-Host "[FAIL] $Msg" -ForegroundColor Red
    exit 1
}
function Info([string]$Msg) {
    Write-Host "[ OK ] $Msg" -ForegroundColor Green
}
function Step([string]$Msg) {
    Write-Host "--- $Msg ---"
}
function Rollback([string]$Reason) {
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $incompat = Join-Path $Runtime "extracted.incompatible-$stamp"
    if (Test-Path -LiteralPath $Extracted) {
        Move-Item -LiteralPath $Extracted -Destination $incompat
        Write-Host "已将不兼容的 extracted 移至：$incompat"
    }
    if ($BackupPath -and (Test-Path -LiteralPath $BackupPath)) {
        Move-Item -LiteralPath $BackupPath -Destination $Extracted
        Write-Host "已回滚备份：$BackupPath -> $Extracted"
    }
    Fail $Reason
}

function Remove-Tree([string]$Path) {
    if (-not (Test-Path -LiteralPath $Path)) { return }
    Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
    if (Test-Path -LiteralPath $Path) {
        $empty = Join-Path $env:TEMP ("empty-" + [guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $empty -Force | Out-Null
        & robocopy $empty $Path /MIR /NFL /NDL /NJH /NJS /NP | Out-Null
        Remove-Item -LiteralPath $Path -Recurse -Force -ErrorAction SilentlyContinue
        Remove-Item -LiteralPath $empty -Recurse -Force -ErrorAction SilentlyContinue
        if (Test-Path -LiteralPath $Path) { Fail "无法清空目录：$Path" }
    }
}

# ---------- QQ 版本：qq-root.txt -> versions\config.json（缺失即退出，不 fallback）----------
Step '读取 QQ 版本'
$qqRootCfg = Join-Path $BotRoot 'qq-root.txt'
if (-not (Test-Path -LiteralPath $qqRootCfg)) { Fail "缺少配置文件：$qqRootCfg" }
$firstLine = Get-Content -LiteralPath $qqRootCfg -Encoding utf8 | Where-Object { $_.Trim() } | Select-Object -First 1
if (-not $firstLine) { Fail "配置文件为空：$qqRootCfg" }
$QQRoot = $firstLine.Trim()
if (-not (Test-Path -LiteralPath $QQRoot)) { Fail "QQ 根目录不存在：$QQRoot" }

$verCfgPath = Join-Path $QQRoot 'versions\config.json'
if (-not (Test-Path -LiteralPath $verCfgPath)) { Fail "缺少版本配置：$verCfgPath" }
$verCfg = Get-Content -LiteralPath $verCfgPath -Raw -Encoding utf8 | ConvertFrom-Json
$QQVersion = "$($verCfg.curVersion)"
if (-not $QQVersion) { Fail "versions\config.json 缺少 curVersion" }
Info "QQ 版本：$QQVersion（$QQRoot）"

# ---------- 版本选择 ----------
if ($Latest) {
    $latestTag = gh release view --repo $Repo --json tagName --jq '.tagName'
    if ($LASTEXITCODE -ne 0 -or -not $latestTag) { Fail "获取最新 release tag 失败" }
    $Version = "$latestTag".Trim()
    Info "使用最新版本：$Version"
}

# ---------- 运行时目录 ----------
if (-not (Test-Path -LiteralPath $Runtime)) { New-Item -ItemType Directory -Path $Runtime -Force | Out-Null }

# ---------- 远端资产信息 ----------
Step "查询远端资产：$Version"
$jq = '.assets[]|select(.name=="' + $AssetName + '")|"\(.size) \(.digest // "")"'
$remoteLine = gh api "repos/$Repo/releases/tags/$Version" --jq $jq
if ($LASTEXITCODE -ne 0) { Fail "查询 release 失败：$Version（仓库 $Repo）" }
$remoteLine = ($remoteLine | Select-Object -First 1)
if (-not $remoteLine) { Fail "release $Version 中未找到 $AssetName" }
$parts = "$remoteLine".Trim() -split '\s+', 2
$RemoteSize = [long]$parts[0]
$RemoteDigest = ""
if ($parts.Count -gt 1) { $RemoteDigest = "$($parts[1])".Trim() }
Write-Host "远端大小：$RemoteSize B；远端摘要：$(if ($RemoteDigest) { $RemoteDigest } else { '（无）' })"

# ---------- 下载判定 ----------
$needDownload = $true
if (-not $Force) {
    if (Test-Path -LiteralPath $Zip) {
        $localSize = (Get-Item -LiteralPath $Zip).Length
        if ($localSize -eq $RemoteSize) {
            $digestOk = $true
            if ($RemoteDigest -match '^sha256:([0-9a-fA-F]{64})$') {
                $expect = $Matches[1].ToLower()
                $actual = (Get-FileHash -LiteralPath $Zip -Algorithm SHA256).Hash.ToLower()
                $digestOk = ($actual -eq $expect)
                if (-not $digestOk) { Write-Host "本地 zip 摘要不匹配（本地 $actual，远端 $expect）" }
            }
            if ($digestOk) {
                Write-Host "复用本地 zip：$Zip（$localSize B）"
                $needDownload = $false
            }
        } else {
            Write-Host "本地 zip 大小不匹配（本地 $localSize B，远端 $RemoteSize B）"
        }
    }
}
if (-not $needDownload) { Info '跳过下载，复用本地 zip' }

if ($needDownload) {
    Step "下载 $Version / $AssetName"
    gh release download $Version --repo $Repo --pattern $AssetName --dir $Runtime --clobber
    if ($LASTEXITCODE -ne 0) { Fail "下载失败：$Version / $AssetName" }
    if (-not (Test-Path -LiteralPath $Zip)) { Fail "下载后未找到：$Zip" }
    $newSize = (Get-Item -LiteralPath $Zip).Length
    if ($newSize -ne $RemoteSize) { Fail "下载文件大小异常：本地 $newSize B，远端 $RemoteSize B" }
    Info "下载完成：$newSize B"
}

# ---------- 解压 ----------
Step '解压到 _incoming'
Remove-Tree -Path $Incoming
New-Item -ItemType Directory -Path $Incoming -Force | Out-Null
Expand-Archive -LiteralPath $Zip -DestinationPath $Incoming -Force
$IncomingExtracted = Join-Path $Incoming 'extracted'
if (-not (Test-Path -LiteralPath $IncomingExtracted)) {
    # 上游 zip 根目录即内容：整体归位为 _incoming\extracted
    $RootTemp = Join-Path $Runtime '_incoming_root'
    Remove-Tree -Path $RootTemp
    Move-Item -LiteralPath $Incoming -Destination $RootTemp
    New-Item -ItemType Directory -Path $Incoming -Force | Out-Null
    Move-Item -LiteralPath $RootTemp -Destination $IncomingExtracted
}
if (-not (Test-Path -LiteralPath $IncomingExtracted)) { Fail "解压结果缺少 extracted 目录：$IncomingExtracted" }
Info '解压完成'

# ---------- 备份旧版本并替换 ----------
Step '备份并替换 extracted'
if (Test-Path -LiteralPath $Extracted) {
    $CacheDir = Join-Path $BotRoot '.cache'
    if (-not (Test-Path -LiteralPath $CacheDir)) { New-Item -ItemType Directory -Path $CacheDir -Force | Out-Null }
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $BackupPath = Join-Path $CacheDir "napcat-extracted-backup-$stamp"
    Move-Item -LiteralPath $Extracted -Destination $BackupPath
    Info "已备份旧 extracted -> $BackupPath"
} else {
    Write-Host '未发现旧 extracted，跳过备份'
}
Move-Item -LiteralPath $IncomingExtracted -Destination $Extracted
Remove-Tree -Path $Incoming
Info "已替换 extracted：$Extracted"

# ---------- 兼容性自检（失败即回滚）----------
Step '兼容性自检'
$mjs = Join-Path $Extracted 'napcat.mjs'
if (-not (Test-Path -LiteralPath $mjs)) { Rollback "缺少文件：$mjs" }
$mjsText = Get-Content -LiteralPath $mjs -Raw -Encoding utf8
$hitBase = $mjsText.Contains($QQVersion)
$hitArch = $mjsText.Contains("$QQVersion-x64")
if (-not ($hitBase -or $hitArch)) {
    Rollback "NapCat $Version 不含 QQ $QQVersion 兼容条目，已回滚"
}
Info "兼容性自检通过：$QQVersion"

# ---------- 关键文件校验 ----------
Step '关键文件校验'
foreach ($f in $RequiredFiles) {
    $p = Join-Path $Extracted $f
    if (-not (Test-Path -LiteralPath $p)) { Rollback "关键文件缺失：$f（$p）" }
    Write-Host "[ OK ] $f"
}

# ---------- 摘要 ----------
$fileCount = (Get-ChildItem -LiteralPath $Extracted -Recurse -File).Count
$localZipSize = (Get-Item -LiteralPath $Zip).Length
Step '摘要'
Write-Host "NapCat 版本 : $Version"
Write-Host "QQ 版本     : $QQVersion"
Write-Host "zip 大小    : $localZipSize B（远端 $RemoteSize B）"
Write-Host "extracted   : $fileCount 个文件"
Write-Host "备份路径    : $(if ($BackupPath) { $BackupPath } else { '（无旧版本，未备份）' })"
Info '完成'
exit 0
