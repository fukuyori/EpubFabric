<#
.SYNOPSIS
EpubFabric CLI の Windows インストーラー（Inno Setup）を作成する。

.DESCRIPTION
publish.ps1 で自己完結型の実行ファイル一式を作成した後、Inno Setup（ISCC.exe）で
セットアップEXEを publish\installer\ に出力する。インストーラーは日本語/英語対応で、
PATH 環境変数への追加（任意タスク）とアンインストール時の除去を行う。

Inno Setup 6 のインストールが必要: https://jrsoftware.org/isinfo.php

.PARAMETER Version
インストーラーのバージョン。省略時は Directory.Build.props の <Version> を使う。

.PARAMETER SkipPublish
publish を省略し、既存の publish 出力からインストーラーだけを作り直す。

.PARAMETER SkipTests
publish 前のテスト実行を省略する。

.EXAMPLE
.\scripts\build-installer.ps1 -Version 1.0.0
.\scripts\build-installer.ps1 -SkipPublish
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipPublish,
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $props = Join-Path $repoRoot "Directory.Build.props"
    $Version = ([xml](Get-Content $props)).Project.PropertyGroup.Version
    if (-not $Version) {
        throw "バージョンを特定できません。-Version を指定するか Directory.Build.props に <Version> を定義してください。"
    }
}
$publishDirectory = Join-Path $repoRoot "publish\EpubFabric.Cli\win-x64"
$installerDirectory = Join-Path $repoRoot "publish\installer"

$isccCandidates = @(
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$iscc = $isccCandidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    $iscc = (Get-Command iscc -ErrorAction SilentlyContinue)?.Source
}
if (-not $iscc) {
    throw "Inno Setup 6（ISCC.exe）が見つかりません。https://jrsoftware.org/isinfo.php からインストールしてください。"
}

if (-not $SkipPublish) {
    $publishArgs = @{}
    if ($SkipTests) { $publishArgs["SkipTests"] = $true }
    & (Join-Path $PSScriptRoot "publish.ps1") @publishArgs
}

if (-not (Test-Path (Join-Path $publishDirectory "epubfabric.exe"))) {
    throw "publish 出力が見つかりません: $publishDirectory（先に scripts\publish.ps1 を実行してください）"
}

New-Item -ItemType Directory -Force $installerDirectory | Out-Null

Write-Host "Inno Setup でインストーラーを作成しています..." -ForegroundColor Cyan
& $iscc `
    "/DAppVersion=$Version" `
    "/DPublishDir=$publishDirectory" `
    "/DOutputDir=$installerDirectory" `
    (Join-Path $PSScriptRoot "installer.iss")
if ($LASTEXITCODE -ne 0) {
    throw "ISCC が失敗しました（終了コード: $LASTEXITCODE）。"
}

$setupExe = Join-Path $installerDirectory "EpubFabric-Setup-$Version.exe"
Write-Host ""
Write-Host "完了しました。" -ForegroundColor Green
Write-Host ("  インストーラー: {0}" -f $setupExe)
Write-Host ("  サイズ        : {0:N1} MB" -f ((Get-Item $setupExe).Length / 1MB))
