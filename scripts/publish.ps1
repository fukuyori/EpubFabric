<#
.SYNOPSIS
build.ps1 が作ったバイナリを配布物にする（配布フォルダー、および任意でインストーラー）。

.DESCRIPTION
scripts\build.ps1 -Runtime <RID> でビルド済みの成果物を dotnet publish --no-build で
publish\EpubFabric.Cli\<runtime>\ と publish\EpubFabric.App\<runtime>\ へ並べ直す。
出力先の epubfabric.exe / EpubFabric.App.exe をそのままコピーして配布できる。
-Installer を付けると、続けて Inno Setup でセットアップEXEも作る。

このスクリプトはコンパイルもテストも行わない。ビルドは build.ps1 の担当なので、
先に次を実行しておくこと:

  .\scripts\build.ps1 -Configuration Release -Runtime win-x64

EpubFabricはネイティブライブラリ（PDFium・OpenCV・ONNX Runtime・SkiaSharp）に
依存するため、既定はフォルダ形式で出力する。-SingleFile を指定すると
CLI を単一EXE（初回起動時にネイティブライブラリを一時展開）にまとめる
（GUI は WinUI 3 のため単一EXE化の対象外）。

.PARAMETER Runtime
対象ランタイム識別子。既定は win-x64。build.ps1 に指定したものと揃える。

.PARAMETER Configuration
ビルド構成。既定は Release。build.ps1 に指定したものと揃える。

.PARAMETER SingleFile
CLI を単一EXEにまとめる。

.PARAMETER SkipGui
GUI（EpubFabric.App）を省略し、CLI のみ出力する。

.PARAMETER Installer
Inno Setup（ISCC.exe）でセットアップEXEを publish\installer\ に作る。
日本語/英語対応で、GUI のスタートメニュー/デスクトップショートカット、
PATH 環境変数への追加（任意タスク）とアンインストール時の除去を行う。
Inno Setup 6 のインストールが必要: https://jrsoftware.org/isinfo.php

.PARAMETER Version
インストーラーのバージョン。省略時は Directory.Build.props の <Version> を使う。

.PARAMETER InstallerOnly
配置を省略し、既存の publish 出力からインストーラーだけを作り直す。

.EXAMPLE
.\scripts\publish.ps1
.\scripts\publish.ps1 -SingleFile
.\scripts\publish.ps1 -Installer
.\scripts\publish.ps1 -InstallerOnly -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SingleFile,
    [switch]$SkipGui,
    [switch]$Installer,
    [string]$Version,
    [switch]$InstallerOnly
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\EpubFabric.Cli\EpubFabric.Cli.csproj"
$outputDirectory = Join-Path $repoRoot "publish\EpubFabric.Cli\$Runtime"

# --no-build なので、対象RID・構成のビルド出力が無いと publish は失敗する。
# 原因が分かりにくいエラーになるため、事前に案内する。
function Assert-Built([string]$binDirectory, [string]$assemblyName, [string]$label) {
    if (-not (Test-Path $binDirectory)) {
        throw "$label のビルド出力が見つかりません: $binDirectory`n先に次を実行してください: .\scripts\build.ps1 -Configuration $Configuration -Runtime $Runtime"
    }

    if (-not (Get-ChildItem $binDirectory -Recurse -Filter $assemblyName -ErrorAction SilentlyContinue)) {
        throw "$label のビルド出力に $assemblyName が見つかりません: $binDirectory`n先に次を実行してください: .\scripts\build.ps1 -Configuration $Configuration -Runtime $Runtime"
    }
}

$guiOutputDirectory = Join-Path $repoRoot "publish\EpubFabric.App\$Runtime"

# WinUI 3 は AnyCPU でビルドできないため、RID からプラットフォームを決める。
$platform = switch -Wildcard ($Runtime) {
    "*-x64"   { "x64" }
    "*-x86"   { "x86" }
    "*-arm64" { "ARM64" }
    default   { $null }
}

if ($InstallerOnly) {
    # 既存の publish 出力からインストーラーだけを作り直す。
    $Installer = $true
    $exePath = Get-ChildItem $outputDirectory -Filter "epubfabric*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    $guiExePath = Get-ChildItem $guiOutputDirectory -Filter "EpubFabric.App.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
}
else {

Assert-Built (Join-Path $repoRoot "src\EpubFabric.Cli\bin\$Configuration") "epubfabric.dll" "CLI"

if (Test-Path $outputDirectory) {
    Remove-Item $outputDirectory -Recurse -Force
}

$publishArgs = @(
    "publish", $project,
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--self-contained", "true",
    "--output", $outputDirectory,
    "--no-build",
    "--nologo",
    # DebugType は build.ps1 の配布用ビルドと揃える必要がある。揃えないと publish が
    # 生成されていない .pdb を探しに行って MSB3030 で失敗する。
    "-p:DebugType=none",
    "-p:PublishSingleFile=$($SingleFile.IsPresent)"
)

if ($SingleFile) {
    # ネイティブライブラリ（PDFium・OpenCV・ONNX Runtime等）もEXEに同梱し、
    # 初回起動時に一時ディレクトリへ展開させる。
    $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
}

Write-Host "CLI を配布用にまとめています（$Runtime / $Configuration）..." -ForegroundColor Cyan
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "CLI の publish が失敗しました。build.ps1 -Runtime $Runtime を実行済みか確認してください。"
}

# ランタイム付属の createdump.exe 等を拾わないよう、アプリ本体のEXEを名前で特定する。
$exePath = Get-ChildItem $outputDirectory -Filter "epubfabric*.exe" | Select-Object -First 1
if ($null -eq $exePath) {
    throw "publish 出力に epubfabric の実行ファイルが見つかりません: $outputDirectory"
}

$guiExePath = $null
if (-not $SkipGui) {
    $guiProject = Join-Path $repoRoot "src\EpubFabric.App\EpubFabric.App.csproj"

    if (-not $platform) {
        throw "GUI に対応していないランタイム識別子です: $Runtime"
    }

    Assert-Built (Join-Path $repoRoot "src\EpubFabric.App\bin\$platform\$Configuration") "EpubFabric.App.dll" "GUI"

    if (Test-Path $guiOutputDirectory) {
        Remove-Item $guiOutputDirectory -Recurse -Force
    }

    Write-Host "GUI を配布用にまとめています（$Runtime / $Configuration）..." -ForegroundColor Cyan
    # PublishTrimmed=false: OllamaClient がリフレクションベースの JSON シリアライズを使うため、
    # トリミングすると実行時に Ollama 連携が壊れる（IL2026）。配布物では無効化する。
    dotnet publish $guiProject `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $guiOutputDirectory `
        --no-build `
        --nologo `
        -p:DebugType=none `
        -p:Platform=$platform `
        -p:PublishTrimmed=false
    if ($LASTEXITCODE -ne 0) {
        throw "GUI の publish が失敗しました。build.ps1 -Runtime $Runtime を実行済みか確認してください。"
    }

    $guiExePath = Get-ChildItem $guiOutputDirectory -Filter "EpubFabric.App.exe" | Select-Object -First 1
    if ($null -eq $guiExePath) {
        throw "publish 出力に EpubFabric.App.exe が見つかりません: $guiOutputDirectory"
    }
}

}

# --- インストーラー（任意） -------------------------------------------------
$setupExe = $null
if ($Installer) {
    if (-not $Version) {
        $props = Join-Path $repoRoot "Directory.Build.props"
        $Version = ([xml](Get-Content $props)).Project.PropertyGroup.Version
        if (-not $Version) {
            throw "バージョンを特定できません。-Version を指定するか Directory.Build.props に <Version> を定義してください。"
        }
    }

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

    if (-not (Test-Path (Join-Path $outputDirectory "epubfabric.exe"))) {
        throw "CLI の配布出力が見つかりません: $outputDirectory"
    }
    if (-not (Test-Path (Join-Path $guiOutputDirectory "EpubFabric.App.exe"))) {
        throw "GUI の配布出力が見つかりません: $guiOutputDirectory（インストーラーはCLIとGUIの両方を同梱します）"
    }

    $installerDirectory = Join-Path $repoRoot "publish\installer"
    New-Item -ItemType Directory -Force $installerDirectory | Out-Null

    Write-Host "Inno Setup でインストーラーを作成しています（$Version）..." -ForegroundColor Cyan
    $isccArgs = @(
        "/DAppVersion=$Version",
        "/DPublishDir=$outputDirectory",
        "/DGuiPublishDir=$guiOutputDirectory",
        "/DOutputDir=$installerDirectory"
    )

    $iconFile = Join-Path $repoRoot "src\EpubFabric.App\Assets\AppIcon.ico"
    if (Test-Path $iconFile) {
        $isccArgs += "/DIconFile=$iconFile"
    }

    & $iscc @isccArgs (Join-Path $PSScriptRoot "installer.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC が失敗しました（終了コード: $LASTEXITCODE）。"
    }

    $setupExe = Join-Path $installerDirectory "EpubFabric-Setup-$Version.exe"
}

# --- 結果 -------------------------------------------------------------------
Write-Host ""
Write-Host "完了しました。" -ForegroundColor Green

function Write-Artifact([string]$label, $exe, [string]$directory) {
    if (-not $exe) { return }
    $files = Get-ChildItem $directory -Recurse -File
    Write-Host ("  {0} : {1}" -f $label, $exe.FullName)
    Write-Host ("  {0} サイズ : {1:N1} MB / {2} ファイル" -f $label, (($files | Measure-Object -Sum Length).Sum / 1MB), $files.Count)
}

Write-Artifact "CLI" $exePath $outputDirectory
Write-Artifact "GUI" $guiExePath $guiOutputDirectory

if ($setupExe) {
    Write-Host ("  インストーラー : {0}" -f $setupExe)
    Write-Host ("  インストーラー サイズ : {0:N1} MB" -f ((Get-Item $setupExe).Length / 1MB))
}

if (-not $setupExe) {
    Write-Host ""
    Write-Host "動作確認:"
    if ($exePath) { Write-Host ("  & `"{0}`" info <input.pdf>" -f $exePath.FullName) }
    if ($guiExePath) { Write-Host ("  & `"{0}`"" -f $guiExePath.FullName) }
}
