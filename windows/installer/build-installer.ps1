# Publishes Talk-to-Text and packages it into artifacts\installer\TalkToText-Setup-<version>.exe.
#
# Needs the .NET 10 SDK and Inno Setup 6:
#   winget install Microsoft.DotNet.SDK.10
#   winget install JRSoftware.InnoSetup
param([string]$Version = '0.1.0')

$ErrorActionPreference = 'Stop'
$windows = Split-Path $PSScriptRoot -Parent

$iscc = @(
    "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
    "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
    "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
) | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup' }

$publish = Join-Path $windows 'artifacts\publish'
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }

# Same command CI uses, so the installer ships exactly what CI smoke-tests.
dotnet publish (Join-Path $windows 'src\TalkToText.App\TalkToText.App.csproj') `
    --configuration Release --runtime win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:Version=$Version --output $publish
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed' }

# Start-Process -Wait: the app is a WinExe, so '&' would not wait or set an exit code.
$exe = Join-Path $publish 'TalkToText.exe'
$run = Start-Process -FilePath $exe -ArgumentList '--selftest' -Wait -PassThru -NoNewWindow
if ($run.ExitCode -ne 0) { throw "self-test failed with exit code $($run.ExitCode)" }

& $iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot 'TalkToText.iss')
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed' }

Get-Item (Join-Path $windows "artifacts\installer\TalkToText-Setup-$Version.exe")
