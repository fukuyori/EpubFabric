<#
.SYNOPSIS
EpubFabric（CLI・GUI）の配布用実行ファイルを作成する。

.DESCRIPTION
dotnet publish で自己完結型（.NETランタイム同梱）の実行ファイル一式を
publish\EpubFabric.Cli\<runtime>\ と publish\EpubFabric.App\<runtime>\ に出力する。
出力先の epubfabric.exe / EpubFabric.App.exe をそのままコピーして配布できる。

EpubFabricはネイティブライブラリ（PDFium・OpenCV・ONNX Runtime・SkiaSharp）に
依存するため、既定はフォルダ形式で出力する。-SingleFile を指定すると
CLI を単一EXE（初回起動時にネイティブライブラリを一時展開）にまとめる
（GUI は WinUI 3 のため単一EXE化の対象外）。

.PARAMETER Runtime
対象ランタイム識別子。既定は win-x64。

.PARAMETER Configuration
ビルド構成。既定は Release。

.PARAMETER SingleFile
CLI を単一EXEにまとめる。

.PARAMETER SkipTests
publish 前のテスト実行を省略する。

.PARAMETER SkipGui
GUI（EpubFabric.App）の publish を省略し、CLI のみ出力する。

.EXAMPLE
.\scripts\publish.ps1
.\scripts\publish.ps1 -SingleFile
.\scripts\publish.ps1 -Runtime win-arm64 -SkipTests
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SingleFile,
    [switch]$SkipTests,
    [switch]$SkipGui
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\EpubFabric.Cli\EpubFabric.Cli.csproj"
$outputDirectory = Join-Path $repoRoot "publish\EpubFabric.Cli\$Runtime"

if (-not $SkipTests) {
    Write-Host "テストを実行しています..." -ForegroundColor Cyan
    dotnet test $repoRoot --nologo -v q
    if ($LASTEXITCODE -ne 0) {
        throw "テストが失敗したため publish を中止しました。"
    }
}

if (Test-Path $outputDirectory) {
    Remove-Item $outputDirectory -Recurse -Force
}

$publishArgs = @(
    "publish", $project,
    "--configuration", $Configuration,
    "--runtime", $Runtime,
    "--self-contained", "true",
    "--output", $outputDirectory,
    "-p:PublishSingleFile=$($SingleFile.IsPresent)",
    "-p:DebugType=none"
)

if ($SingleFile) {
    # ネイティブライブラリ（PDFium・OpenCV・ONNX Runtime等）もEXEに同梱し、
    # 初回起動時に一時ディレクトリへ展開させる。
    $publishArgs += "-p:IncludeNativeLibrariesForSelfExtract=true"
}

Write-Host "publish を実行しています（$Runtime / $Configuration）..." -ForegroundColor Cyan
dotnet @publishArgs
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish が失敗しました。"
}

# ランタイム付属の createdump.exe 等を拾わないよう、アプリ本体のEXEを名前で特定する。
$exePath = Get-ChildItem $outputDirectory -Filter "epubfabric*.exe" | Select-Object -First 1
if ($null -eq $exePath) {
    throw "publish 出力に epubfabric の実行ファイルが見つかりません: $outputDirectory"
}

$guiExePath = $null
if (-not $SkipGui) {
    $guiProject = Join-Path $repoRoot "src\EpubFabric.App\EpubFabric.App.csproj"
    $guiOutputDirectory = Join-Path $repoRoot "publish\EpubFabric.App\$Runtime"

    # WinUI 3 は AnyCPU でビルドできないため、RID からプラットフォームを決める。
    $platform = switch -Wildcard ($Runtime) {
        "*-x64"   { "x64" }
        "*-x86"   { "x86" }
        "*-arm64" { "ARM64" }
        default   { throw "GUI の publish に対応していないランタイムです: $Runtime" }
    }

    if (Test-Path $guiOutputDirectory) {
        Remove-Item $guiOutputDirectory -Recurse -Force
    }

    Write-Host "GUI の publish を実行しています（$Runtime / $Configuration）..." -ForegroundColor Cyan
    # PublishTrimmed=false: OllamaClient がリフレクションベースの JSON シリアライズを使うため、
    # トリミングすると実行時に Ollama 連携が壊れる（IL2026）。配布ビルドでは無効化する。
    dotnet publish $guiProject `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $guiOutputDirectory `
        -p:Platform=$platform `
        -p:PublishTrimmed=false `
        -p:DebugType=none
    if ($LASTEXITCODE -ne 0) {
        throw "GUI の dotnet publish が失敗しました。"
    }

    $guiExePath = Get-ChildItem $guiOutputDirectory -Filter "EpubFabric.App.exe" | Select-Object -First 1
    if ($null -eq $guiExePath) {
        throw "publish 出力に EpubFabric.App.exe が見つかりません: $guiOutputDirectory"
    }
}

$totalSize = (Get-ChildItem $outputDirectory -Recurse -File | Measure-Object -Sum Length).Sum

Write-Host ""
Write-Host "完了しました。" -ForegroundColor Green
Write-Host ("  CLI 実行ファイル : {0}" -f $exePath.FullName)
Write-Host ("  CLI 合計サイズ   : {0:N1} MB / {1} ファイル" -f ($totalSize / 1MB), (Get-ChildItem $outputDirectory -Recurse -File).Count)
if ($guiExePath) {
    $guiTotalSize = (Get-ChildItem $guiOutputDirectory -Recurse -File | Measure-Object -Sum Length).Sum
    Write-Host ("  GUI 実行ファイル : {0}" -f $guiExePath.FullName)
    Write-Host ("  GUI 合計サイズ   : {0:N1} MB / {1} ファイル" -f ($guiTotalSize / 1MB), (Get-ChildItem $guiOutputDirectory -Recurse -File).Count)
}
Write-Host ""
Write-Host "動作確認:"
Write-Host ("  & `"{0}`" info <input.pdf>" -f $exePath.FullName)
if ($guiExePath) {
    Write-Host ("  & `"{0}`"" -f $guiExePath.FullName)
}
