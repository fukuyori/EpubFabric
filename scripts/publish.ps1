<#
.SYNOPSIS
EpubFabric CLI の配布用実行ファイルを作成する。

.DESCRIPTION
dotnet publish で自己完結型（.NETランタイム同梱）の実行ファイル一式を
publish\EpubFabric.Cli\<runtime>\ に出力する。出力先の epubfabric.exe を
そのままコピーして配布できる。

EpubFabricはネイティブライブラリ（PDFium・OpenCV・ONNX Runtime・SkiaSharp）に
依存するため、既定はフォルダ形式で出力する。-SingleFile を指定すると
単一EXE（初回起動時にネイティブライブラリを一時展開）にまとめる。

.PARAMETER Runtime
対象ランタイム識別子。既定は win-x64。

.PARAMETER Configuration
ビルド構成。既定は Release。

.PARAMETER SingleFile
単一EXEにまとめる。

.PARAMETER SkipTests
publish 前のテスト実行を省略する。

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
    [switch]$SkipTests
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
$totalSize = (Get-ChildItem $outputDirectory -Recurse -File | Measure-Object -Sum Length).Sum

Write-Host ""
Write-Host "完了しました。" -ForegroundColor Green
Write-Host ("  実行ファイル : {0}" -f $exePath.FullName)
Write-Host ("  合計サイズ   : {0:N1} MB / {1} ファイル" -f ($totalSize / 1MB), (Get-ChildItem $outputDirectory -Recurse -File).Count)
Write-Host ""
Write-Host "動作確認:"
Write-Host ("  & `"{0}`" info <input.pdf>" -f $exePath.FullName)
