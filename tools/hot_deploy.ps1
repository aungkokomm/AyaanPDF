# Fast inner-loop deploy: publish a self-contained Release build and copy it
# straight over the installed app, skipping the ~2-minute Inno Setup compression.
#
# The installed app's binaries live in %LOCALAPPDATA%\Programs\Ayaan PDF, but its
# USER DATA (stamp images) lives in %LOCALAPPDATA%\Ayaan PDF\Stamps, a different
# folder, so copying binaries here cannot touch it. Even so, Stamps and the log
# files are excluded defensively, in case a future build ever writes them beside
# the exe. No purge: stale files are harmless and never worth risking data for.
#
# Ship REAL updates to users with build_installer.ps1; this is for testing on
# this machine only.
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent

$target = Join-Path $env:LOCALAPPDATA 'Programs\Ayaan PDF'
if (-not (Test-Path $target)) {
    throw "Installed app not found at $target. Install once with the setup first."
}

# Refuse to clobber a running app: a locked exe gives a confusing partial copy.
$proc = Get-Process -Name 'Ayaan PDF' -ErrorAction SilentlyContinue
if ($proc) {
    throw "Ayaan PDF is running (pid $($proc.Id)). Close it, then re-run."
}

$msbuild = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" `
    -latest -prerelease -find MSBuild\**\Bin\MSBuild.exe 2>$null | Select-Object -First 1
if (-not $msbuild) {
    $msbuild = Get-ChildItem "C:\Program Files\Microsoft Visual Studio" -Recurse -Filter MSBuild.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -like "*\Bin\MSBuild.exe" | Select-Object -Expand FullName -First 1
}
if (-not $msbuild) { throw "MSBuild not found." }

$appDir = Join-Path $root 'PdfEditorApp'
$publishDir = Join-Path $appDir 'publish'

Write-Host "==> Publishing self-contained Release build..." -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }
& $msbuild (Join-Path $appDir 'PdfEditorApp.csproj') /t:Publish /restore `
    /p:Configuration=Release /p:Platform=x64 /p:RuntimeIdentifier=win-x64 `
    /p:WindowsPackageType=None /p:WindowsAppSDKSelfContained=true /p:SelfContained=true `
    /p:PublishTrimmed=false /p:PublishReadyToRun=false /p:PublishSingleFile=false `
    /p:AppxPackage=false /p:GenerateAppxPackageOnBuild=false `
    "/p:PublishDir=$publishDir\" /v:minimal /nologo
if ($LASTEXITCODE -ne 0) { throw "Publish failed (exit $LASTEXITCODE)." }

Write-Host "==> Copying over $target ..." -ForegroundColor Cyan
# /E recurse, no purge; exclude user data and logs.
robocopy $publishDir $target /E /XD Stamps /XF crash.log diag.log /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { throw "robocopy failed (exit $LASTEXITCODE)." }

$ver = (Get-Item (Join-Path $target 'Ayaan PDF.exe')).VersionInfo.FileVersion
Write-Host "==> Deployed $ver to $target" -ForegroundColor Green
