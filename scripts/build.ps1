<#
.SYNOPSIS
EpubFabric を開発用にビルドし、テストを実行する。

.DESCRIPTION
ソリューション（EpubFabric.slnx）全体を復元・ビルドし、単体テストを実行する。
CLI・GUI・テストがまとめて対象になる。

コンパイルはすべてこのスクリプトが行う。scripts\publish.ps1 はコンパイルせず
（dotnet publish --no-build）、ここでできた成果物を配布用に並べ直すだけなので、
配布物を作る前には -Runtime を付けて配布用ビルドを作っておく必要がある。

  手元で動かすビルド : .\scripts\build.ps1
  配布用ビルド       : .\scripts\build.ps1 -Configuration Release -Runtime win-x64

.PARAMETER Configuration
ビルド構成。既定は Debug。

.PARAMETER Runtime
配布用ビルドの対象ランタイム識別子（win-x64 等）。指定すると、publish.ps1 が
そのまま配布できるよう、自己完結型（.NETランタイム同梱）でCLIとGUIをビルドする。
未指定なら通常の開発用ビルド（フレームワーク依存）。

.PARAMETER SkipTests
テストの実行を省略し、ビルドだけを行う。

.PARAMETER TestFilter
実行するテストを絞り込む（dotnet test --filter に渡す）。

.PARAMETER Clean
ビルド前に bin / obj を削除してからビルドする（生成物の取り違えを疑うときに使う）。

.EXAMPLE
.\scripts\build.ps1
.\scripts\build.ps1 -Configuration Release
.\scripts\build.ps1 -Configuration Release -Runtime win-x64
.\scripts\build.ps1 -TestFilter ColumnDetectorTests
.\scripts\build.ps1 -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [string]$Runtime,
    [switch]$SkipTests,
    [string]$TestFilter,
    [switch]$Clean
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$solution = Join-Path $repoRoot "EpubFabric.slnx"
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

if ($Clean) {
    Write-Host "bin / obj を削除しています..." -ForegroundColor Cyan
    Get-ChildItem $repoRoot -Directory -Recurse -Include bin, obj -ErrorAction SilentlyContinue |
        Where-Object { $_.FullName -notlike "*\publish\*" } |
        ForEach-Object { Remove-Item $_.FullName -Recurse -Force -ErrorAction SilentlyContinue }
}

Write-Host "復元しています..." -ForegroundColor Cyan
dotnet restore $solution
if ($LASTEXITCODE -ne 0) {
    throw "dotnet restore が失敗しました。"
}

Write-Host "ビルドしています（$Configuration）..." -ForegroundColor Cyan
dotnet build $solution --configuration $Configuration --no-restore --nologo
if ($LASTEXITCODE -ne 0) {
    throw "dotnet build が失敗しました。"
}

if ($Runtime) {
    # 配布用ビルド。publish.ps1 は --no-build でこの出力を並べ直すだけなので、
    # 自己完結型（ランタイム同梱）であることも含めてここで確定させる。
    # WinUI 3 は AnyCPU でビルドできないため、RID からプラットフォームを決める。
    $platform = switch -Wildcard ($Runtime) {
        "*-x64"   { "x64" }
        "*-x86"   { "x86" }
        "*-arm64" { "ARM64" }
        default   { throw "対応していないランタイム識別子です: $Runtime" }
    }

    $distributionProjects = @(
        @{ Name = "CLI"; Path = Join-Path $repoRoot "src\EpubFabric.Cli\EpubFabric.Cli.csproj"; Extra = @() },
        @{ Name = "GUI"; Path = Join-Path $repoRoot "src\EpubFabric.App\EpubFabric.App.csproj"; Extra = @("-p:Platform=$platform") }
    )

    foreach ($project in $distributionProjects) {
        Write-Host "配布用にビルドしています（$($project.Name) / $Runtime / $Configuration）..." -ForegroundColor Cyan

        # RID別の復元資産（project.assets.json）が必要なため、ここでは --no-restore にしない。
        # DebugType=none: 配布物に .pdb を含めない（publish は --no-build なので、
        # ここで決めておかないと配布物へ持ち込まれる）。
        $buildArgs = @(
            "build", $project.Path,
            "--configuration", $Configuration,
            "--runtime", $Runtime,
            "--self-contained", "true",
            "--nologo",
            "-p:DebugType=none"
        ) + $project.Extra

        dotnet @buildArgs
        if ($LASTEXITCODE -ne 0) {
            throw "$($project.Name) の配布用ビルドが失敗しました。"
        }
    }
}

if (-not $SkipTests) {
    Write-Host "テストを実行しています..." -ForegroundColor Cyan

    $testArgs = @("test", $solution, "--configuration", $Configuration, "--no-build", "--nologo")
    if ($TestFilter) {
        $testArgs += @("--filter", $TestFilter)
    }

    dotnet @testArgs
    if ($LASTEXITCODE -ne 0) {
        throw "テストが失敗しました。"
    }
}

$stopwatch.Stop()

# 実行してすぐ動かせるよう、生成された実行ファイルの場所を示す。
$cliExe = Get-ChildItem (Join-Path $repoRoot "src\EpubFabric.Cli\bin\$Configuration") -Recurse -Filter "epubfabric.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
$guiExe = Get-ChildItem (Join-Path $repoRoot "src\EpubFabric.App\bin") -Recurse -Filter "EpubFabric.App.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\$Configuration\*" } |
    Select-Object -First 1

Write-Host ""
Write-Host "完了しました（$([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) 秒）。" -ForegroundColor Green
if ($cliExe) { Write-Host ("  CLI : {0}" -f $cliExe.FullName) }
if ($guiExe) { Write-Host ("  GUI : {0}" -f $guiExe.FullName) }
Write-Host ""

if ($Runtime) {
    Write-Host "配布物にまとめる: .\scripts\publish.ps1 -Configuration $Configuration -Runtime $Runtime"
}
else {
    Write-Host "配布用ビルドを作る場合: .\scripts\build.ps1 -Configuration Release -Runtime win-x64"
}
