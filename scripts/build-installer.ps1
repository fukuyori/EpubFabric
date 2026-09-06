<#
.SYNOPSIS
build.ps1 が作ったバイナリから配布フォルダーと Inno Setup インストーラーを作る。

.DESCRIPTION
scripts\build.ps1 でビルド済みの成果物を dotnet publish --no-build で
publish\EpubFabric.Cli\<runtime>\ と publish\EpubFabric.App\<runtime>\ へ並べ直し、
続けて Inno Setup で publish\installer\ にセットアップEXEを作る。
配布フォルダーの EpubFabric.exe（GUI）/ epubfabric-cli.exe（CLI）はそのままコピーしても使える。
インストーラーが不要なときは -SkipInstaller を指定する。

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

.PARAMETER SkipInstaller
インストーラーの作成を省略し、配布フォルダーの出力だけで終える。
既定では Inno Setup（ISCC.exe）でセットアップEXEを publish\installer\ まで作る。
インストーラーは日本語/英語対応で、GUI のスタートメニュー/デスクトップショートカット、
PATH 環境変数への追加（任意タスク）とアンインストール時の除去を行う。
Inno Setup 6 のインストールが必要: https://jrsoftware.org/isinfo.php
（-SkipGui を指定した場合は、インストーラーがGUIを同梱できないため自動的に省略する。）

.PARAMETER Version
インストーラーのバージョン。省略時は Directory.Build.props の <Version> を使う。

.PARAMETER InstallerOnly
配置を省略し、既存の publish 出力からインストーラーだけを作り直す。

.PARAMETER Sign
Windows SDK の signtool.exe で CLI・GUI の実行ファイルを署名し、Inno Setup の
SignTool / SignedUninstaller 機能でインストーラーとアンインストーラーも署名する。
証明書サムプリントを省略した場合は、Windows 証明書ストアから最適なコード署名証明書を
自動選択する。PFXファイルやパスワードは扱わない。

.PARAMETER CertificateThumbprint
署名に使う証明書のSHA-1サムプリント。-Sign 指定時のみ使用する。
省略時は signtool.exe の /a で証明書を自動選択する。

.PARAMETER TimestampUrl
RFC 3161 タイムスタンプサーバーのURL。-Sign 指定時のみ使用する。
省略時はタイムスタンプを付けずに署名する。

.EXAMPLE
.\scripts\build-installer.ps1
.\scripts\build-installer.ps1 -Sign
.\scripts\build-installer.ps1 -Sign -CertificateThumbprint 0123456789ABCDEF0123456789ABCDEF01234567 -TimestampUrl https://timestamp.example.com
.\scripts\build-installer.ps1 -SkipInstaller
.\scripts\build-installer.ps1 -SingleFile
.\scripts\build-installer.ps1 -InstallerOnly -Version 1.0.0
#>
[CmdletBinding()]
param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [switch]$SingleFile,
    [switch]$SkipGui,
    [switch]$SkipInstaller,
    [string]$Version,
    [switch]$InstallerOnly,
    [switch]$Sign,
    [string]$CertificateThumbprint,
    [string]$TimestampUrl
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\EpubFabric.Cli\EpubFabric.Cli.csproj"
$outputDirectory = Join-Path $repoRoot "publish\EpubFabric.Cli\$Runtime"

if (-not $Sign -and ($CertificateThumbprint -or $TimestampUrl)) {
    throw "-CertificateThumbprint と -TimestampUrl は -Sign と一緒に指定してください。"
}

if ($CertificateThumbprint) {
    $CertificateThumbprint = $CertificateThumbprint -replace '\s', ''
    if ($CertificateThumbprint -notmatch '^[0-9A-Fa-f]{40}$') {
        throw "証明書サムプリントは40桁の16進数で指定してください。"
    }
}

if ($TimestampUrl) {
    $timestampUri = $null
    if (-not [Uri]::TryCreate($TimestampUrl, [UriKind]::Absolute, [ref]$timestampUri) -or
        $timestampUri.Scheme -notin @("http", "https")) {
        throw "タイムスタンプURLは http または https の絶対URLで指定してください: $TimestampUrl"
    }
}

function Find-SignTool {
    $command = Get-Command signtool.exe -ErrorAction SilentlyContinue
    if ($command) {
        return $command.Source
    }

    $sdkBin = "${env:ProgramFiles(x86)}\Windows Kits\10\bin"
    if (Test-Path -LiteralPath $sdkBin) {
        $candidate = Get-ChildItem -LiteralPath $sdkBin -Directory -ErrorAction SilentlyContinue |
            Sort-Object Name -Descending |
            ForEach-Object { Join-Path $_.FullName "x64\signtool.exe" } |
            Where-Object { Test-Path -LiteralPath $_ } |
            Select-Object -First 1
        if ($candidate) {
            return $candidate
        }
    }

    $appCertificationKit = "${env:ProgramFiles(x86)}\Windows Kits\10\App Certification Kit\signtool.exe"
    if (Test-Path -LiteralPath $appCertificationKit) {
        return $appCertificationKit
    }

    throw "signtool.exe が見つかりません。Windows SDK をインストールしてください。"
}

function Get-SignArguments([string]$filePath) {
    $arguments = @("sign")
    if ($CertificateThumbprint) {
        $arguments += @("/sha1", $CertificateThumbprint)
    }
    else {
        $arguments += "/a"
    }

    $arguments += @("/fd", "SHA256", "/d", "EpubFabric")
    if ($TimestampUrl) {
        $arguments += @("/tr", $TimestampUrl, "/td", "SHA256")
    }
    $arguments += $filePath
    return $arguments
}

function Get-InnoSignCommand([string]$signToolPath) {
    $selection = if ($CertificateThumbprint) {
        "/sha1 $CertificateThumbprint"
    }
    else {
        "/a"
    }

    $command = '$q' + $signToolPath + '$q sign ' + $selection + ' /fd SHA256 /d $qEpubFabric$q'
    if ($TimestampUrl) {
        $command += ' /tr $q' + $TimestampUrl + '$q /td SHA256'
    }
    return $command + ' $q$f$q'
}

function Invoke-CodeSigning([string]$signToolPath, [string]$filePath, [string]$label) {
    if (-not (Test-Path -LiteralPath $filePath -PathType Leaf)) {
        throw "$label が見つかりません: $filePath"
    }

    Write-Host "$label に電子署名しています..." -ForegroundColor Cyan
    $signArguments = Get-SignArguments $filePath
    & $signToolPath @signArguments
    if ($LASTEXITCODE -ne 0) {
        throw "$label の電子署名に失敗しました（終了コード: $LASTEXITCODE）。"
    }

    & $signToolPath verify /pa $filePath
    if ($LASTEXITCODE -ne 0) {
        throw "$label の署名検証に失敗しました（終了コード: $LASTEXITCODE）。"
    }
}

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

# インストーラーは既定で作る。GUIを省いた場合は同梱できないため作らない。
$buildInstaller = -not $SkipInstaller -and -not $SkipGui
if ($SkipGui -and -not $SkipInstaller) {
    Write-Host "GUI を省略したため、インストーラーの作成も省略します（インストーラーはCLIとGUIを同梱します）。" -ForegroundColor Yellow
}

if ($InstallerOnly) {
    # 既存の publish 出力からインストーラーだけを作り直す。
    $buildInstaller = $true
    $exePath = Get-ChildItem $outputDirectory -Filter "epubfabric*.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
    $guiExePath = Get-ChildItem $guiOutputDirectory -Filter "EpubFabric.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
}
else {

Assert-Built (Join-Path $repoRoot "src\EpubFabric.Cli\bin\$Configuration") "epubfabric-cli.dll" "CLI"

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

    Assert-Built (Join-Path $repoRoot "src\EpubFabric.App\bin\$platform\$Configuration") "EpubFabric.dll" "GUI"

    if (Test-Path $guiOutputDirectory) {
        Remove-Item $guiOutputDirectory -Recurse -Force
    }

    Write-Host "GUI を配布用にまとめています（$Runtime / $Configuration）..." -ForegroundColor Cyan
    # トリミングの可否は EpubFabric.App.csproj 側で決まる（ビルド時に runtimeconfig へ
    # 焼き込まれるため、--no-build のここでは変えられない）。
    dotnet publish $guiProject `
        --configuration $Configuration `
        --runtime $Runtime `
        --self-contained true `
        --output $guiOutputDirectory `
        --no-build `
        --nologo `
        -p:DebugType=none `
        -p:Platform=$platform
    if ($LASTEXITCODE -ne 0) {
        throw "GUI の publish が失敗しました。build.ps1 -Runtime $Runtime を実行済みか確認してください。"
    }

    $guiExePath = Get-ChildItem $guiOutputDirectory -Filter "EpubFabric.exe" | Select-Object -First 1
    if ($null -eq $guiExePath) {
        throw "publish 出力に EpubFabric.App.exe が見つかりません: $guiOutputDirectory"
    }
}

}

$signToolPath = $null
if ($Sign) {
    $signToolPath = Find-SignTool
    Write-Host "署名ツール: $signToolPath" -ForegroundColor Cyan

    if ($exePath) {
        Invoke-CodeSigning $signToolPath $exePath.FullName "CLI実行ファイル"
    }
    if ($guiExePath) {
        Invoke-CodeSigning $signToolPath $guiExePath.FullName "GUI実行ファイル"
    }
}

# --- インストーラー（任意） -------------------------------------------------
$setupExe = $null
if ($buildInstaller) {
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
        throw "Inno Setup 6（ISCC.exe）が見つかりません。https://jrsoftware.org/isinfo.php からインストールするか、-SkipInstaller を指定してください。"
    }

    if (-not (Test-Path (Join-Path $outputDirectory "epubfabric-cli.exe"))) {
        throw "CLI の配布出力が見つかりません: $outputDirectory"
    }
    if (-not (Test-Path (Join-Path $guiOutputDirectory "EpubFabric.exe"))) {
        throw "GUI の配布出力が見つかりません: $guiOutputDirectory（インストーラーの本体はGUIです）"
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

    if ($Sign) {
        $innoSignCommand = Get-InnoSignCommand $signToolPath
        $isccArgs += "/Sepubfabric=$innoSignCommand"
        $isccArgs += "/DSignToolName=epubfabric"
    }

    & $iscc @isccArgs (Join-Path $PSScriptRoot "installer.iss")
    if ($LASTEXITCODE -ne 0) {
        throw "ISCC が失敗しました（終了コード: $LASTEXITCODE）。"
    }

    $setupExe = Join-Path $installerDirectory "EpubFabric-Setup-$Version.exe"
    if ($Sign) {
        & $signToolPath verify /pa $setupExe
        if ($LASTEXITCODE -ne 0) {
            throw "インストーラーの署名検証に失敗しました（終了コード: $LASTEXITCODE）。"
        }
    }
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
