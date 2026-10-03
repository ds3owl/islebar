# Builds dist\IsleBar-Setup-<version>.exe: publishes the app (self-contained — no .NET or Windows App Runtime needed on the
# target PC) and the single-file CLI into build\, then compiles installer\IsleBar.iss with Inno Setup 6.
#
#   .\scripts\build-installer.ps1                 # version 0.1.0
#   .\scripts\build-installer.ps1 -Version 0.2.0
#
# Not signed: without a code-signing certificate Windows SmartScreen warns on first run ("unknown publisher"), and a PC with
# Smart App Control on blocks the unsigned exe. Sign build\app\IsleBar.App.exe, build\bin\islebar.exe and the setup exe with
# signtool before publishing once a certificate is available.
param([string]$Version = '0.1.0')

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)' }

$app = Join-Path $repo 'build\app'
$bin = Join-Path $repo 'build\bin'
Remove-Item -Recurse -Force $app, $bin -ErrorAction SilentlyContinue

& $dotnet publish (Join-Path $repo 'src\IsleBar.App') -c Release -r win-x64 --self-contained true -o $app -nologo -v q "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'app publish failed' }
& $dotnet publish (Join-Path $repo 'src\IsleBar.Cli') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o $bin -nologo -v q "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'cli publish failed' }

& $iscc "/DAppVersion=$Version" (Join-Path $repo 'installer\IsleBar.iss')
if ($LASTEXITCODE -ne 0) { throw 'installer compile failed' }
$setup = Join-Path $repo "dist\IsleBar-Setup-$Version.exe"
# the checksum one-click update insists on — attach both files to the GitHub release
$hash = (Get-FileHash $setup -Algorithm SHA256).Hash.ToLowerInvariant()
[IO.File]::WriteAllText("$setup.sha256", "$hash  IsleBar-Setup-$Version.exe`n", (New-Object Text.UTF8Encoding($false)))
Get-Item $setup, "$setup.sha256"
