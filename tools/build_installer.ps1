# Builds the PdfEditor installer end to end:
#   1. Builds render_core.dll in release mode (cargo)
#   2. Publishes an unpackaged, self-contained Release build of PdfEditorApp
#      (app + WinAppSDK + .NET runtime + render_core.dll + pdfium.dll + sample.pdf)
#   3. Compiles installer\PdfEditor.iss with Inno Setup into dist\
#
# Usage:  pwsh -File tools\build_installer.ps1
# Requires: Visual Studio MSBuild (the .NET 10 dotnet CLI lacks the WinUI PRI task),
#           a Rust toolchain (cargo), and Inno Setup 6 (ISCC.exe).
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

# --- locate tools ---
$msbuild = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $msbuild = & $vswhere -latest -prerelease -find MSBuild\**\Bin\MSBuild.exe 2>$null | Select-Object -First 1
}
if (-not $msbuild -or -not (Test-Path $msbuild)) {
    $msbuild = Get-ChildItem "C:\Program Files\Microsoft Visual Studio", "C:\Program Files (x86)\Microsoft Visual Studio" `
        -Recurse -Filter MSBuild.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -like "*\Bin\MSBuild.exe" | Select-Object -Expand FullName -First 1
}
if (-not $msbuild -or -not (Test-Path $msbuild)) { throw "MSBuild not found. Install Visual Studio with the .NET desktop workload." }
Write-Host "Using MSBuild: $msbuild"

$iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
if (-not (Test-Path $iscc)) { throw "Inno Setup 6 (ISCC.exe) not found. Install from https://jrsoftware.org/isdl.php" }

Write-Host "==> Building render_core.dll (release)..." -ForegroundColor Cyan
Push-Location (Join-Path $root 'render_core')
try {
    & cargo build --release
    if ($LASTEXITCODE -ne 0) { throw "cargo build --release failed (exit $LASTEXITCODE)." }
} finally {
    Pop-Location
}

$appDir = Join-Path $root 'PdfEditorApp'
$publishDir = Join-Path $appDir 'publish'

Write-Host "==> Publishing unpackaged self-contained build..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $msbuild (Join-Path $appDir 'PdfEditorApp.csproj') /t:Publish /restore `
    /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 `
    /p:WindowsPackageType=None /p:WindowsAppSDKSelfContained=true /p:SelfContained=true `
    /p:PublishTrimmed=false /p:PublishReadyToRun=false /p:PublishSingleFile=false `
    /p:AppxPackage=false /p:GenerateAppxPackageOnBuild=false `
    "/p:PublishDir=$publishDir\" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

# Must match ExeName in installer\PdfEditor.iss, which reads the version off it.
$exe = Join-Path $publishDir 'Ayaan PDF.exe'
if (-not (Test-Path $exe)) { throw "'Ayaan PDF.exe' missing from publish output." }
foreach ($native in 'render_core.dll', 'pdfium.dll', 'sample.pdf') {
    if (-not (Test-Path (Join-Path $publishDir $native))) { throw "$native missing from publish output." }
}

# The bundled typeface and the notices that must legally travel with it. The
# .iss recurses the whole publish folder, so these ride along on their own, but
# a missing font is a silent fallback to whatever DirectWrite substitutes and a
# missing licence is a licence breach. Neither shows up by looking at the app,
# so both are checked here rather than trusted.
foreach ($asset in 'Oswald-Bold.ttf', 'OFL.txt', 'THIRD-PARTY-NOTICES.txt') {
    if (-not (Test-Path (Join-Path $publishDir "Assets\Fonts\$asset"))) {
        throw "Assets\Fonts\$asset missing from publish output."
    }
}

Write-Host "==> Compiling installer with Inno Setup..." -ForegroundColor Cyan
& $iscc (Join-Path $root 'installer\PdfEditor.iss')
if ($LASTEXITCODE -ne 0) { throw "Inno Setup compile failed (exit $LASTEXITCODE)." }

# Must match OutputBaseFilename in the .iss. This globbed the old
# "PdfEditor-Setup-*" name long after the installer was renamed, so every run
# ended by proudly reporting a stale build from months earlier: the script said
# 1.14.0 while it had just compiled 1.31.0.
$setup = Get-ChildItem (Join-Path $root 'dist') -Filter 'AyaanPDF-Setup-*.exe' |
    Sort-Object LastWriteTime | Select-Object -Last 1
if (-not $setup) { throw "No AyaanPDF-Setup-*.exe in dist after a successful compile." }
Write-Host ("==> Done: {0} ({1} MB)" -f $setup.FullName, [math]::Round($setup.Length / 1MB, 1)) -ForegroundColor Green
