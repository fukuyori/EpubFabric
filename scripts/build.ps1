<#
.SYNOPSIS
EpubFabric をビルドし、テストを実行する。

.DESCRIPTION
ソリューション（EpubFabric.slnx）全体を復元・ビルドし、単体テストを実行する。
CLI・GUI・テストがまとめて対象になる。

コンパイルはすべてこのスクリプトが行う。scripts\publish.ps1 はコンパイルせず
（dotnet publish --no-build）、ここでできた成果物を配布物に仕立てるだけなので、
既定では publish.ps1 の既定（Release / win-x64）に合わせて配布用ビルドまで作る。

  .\scripts\build.ps1      ビルドとテスト
  .\scripts\publish.ps1    配布フォルダーとインストーラーの作成

いずれも引数なしで、この順に実行すればインストーラーまで出来上がる。

デバッグしたいときだけ -Configuration Debug を付ける。

.PARAMETER Configuration
ビルド構成。既定は Release（publish.ps1 の既定に合わせる）。

.PARAMETER Runtime
配布用ビルドの対象ランタイム識別子。既定は win-x64 で、publish.ps1 がそのまま
配布できるよう自己完結型（.NETランタイム同梱）でCLIとGUIをビルドする。
空文字を渡すと配布用ビルドを省略し、通常のビルド（フレームワーク依存）だけを行う。

.PARAMETER SkipTests
テストの実行を省略し、ビルドだけを行う。

.PARAMETER TestFilter
実行するテストを絞り込む（dotnet test --filter に渡す）。

.PARAMETER Clean
ビルド前に bin / obj を削除してからビルドする（生成物の取り違えを疑うときに使う）。

.EXAMPLE
.\scripts\build.ps1
.\scripts\build.ps1 -Configuration Debug
.\scripts\build.ps1 -Runtime "" -SkipTests
.\scripts\build.ps1 -TestFilter ColumnDetectorTests
.\scripts\build.ps1 -Clean
#>
[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [AllowEmptyString()]
    [string]$Runtime = "win-x64",
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
$cliExe = Get-ChildItem (Join-Path $repoRoot "src\EpubFabric.Cli\bin\$Configuration") -Recurse -Filter "epubfabric-cli.exe" -ErrorAction SilentlyContinue |
    Select-Object -First 1
$guiExe = Get-ChildItem (Join-Path $repoRoot "src\EpubFabric.App\bin") -Recurse -Filter "EpubFabric.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -like "*\$Configuration\*" } |
    Select-Object -First 1

Write-Host ""
Write-Host "完了しました（$([math]::Round($stopwatch.Elapsed.TotalSeconds, 1)) 秒）。" -ForegroundColor Green
if ($cliExe) { Write-Host ("  CLI : {0}" -f $cliExe.FullName) }
if ($guiExe) { Write-Host ("  GUI : {0}" -f $guiExe.FullName) }
Write-Host ""

if ($Runtime) {
    # publish.ps1 の既定（Release / win-x64）と同じなら引数は要らない。
    # 違うときだけ、そのまま貼って実行できるよう必要な引数を添える。
    $publishOptions = ""
    if ($Configuration -ne "Release") { $publishOptions += " -Configuration $Configuration" }
    if ($Runtime -ne "win-x64") { $publishOptions += " -Runtime $Runtime" }

    Write-Host ("配布物（フォルダー + インストーラー）を作る: .\scripts\publish.ps1{0}" -f $publishOptions)
}
else {
    Write-Host "配布用ビルドを作る場合: .\scripts\build.ps1 -Configuration $Configuration -Runtime win-x64"
}
