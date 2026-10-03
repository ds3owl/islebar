# Dev launcher (workaround based on measurements on PC 09-29)
# - A PC with Smart App Control on blocks the unsigned IsleBar.App.exe → launch the DLL via the signed dotnet.exe.
# - WinUI then looks for UI resources (resources.pri, *.xbf) "next to the executable (dotnet.exe)", so copy them there.
#   (Copied only into the dev %USERPROFILE%\.dotnet. For the release build this is solved with a signed exe.)
# - Windows App Runtime 1.6 (including DDLM) must be installed.
param([switch]$Build)
$dotnetDir = Join-Path $env:USERPROFILE '.dotnet'
$dotnet = Join-Path $dotnetDir 'dotnet.exe'
$dll = Get-ChildItem -Path (Join-Path $PSScriptRoot 'src\IsleBar.App\bin\Debug') -Recurse -Filter 'IsleBar.App.dll' |
    Where-Object { $_.DirectoryName -notlike '*\win-*' } | Select-Object -First 1
$out = $dll.DirectoryName

Get-CimInstance Win32_Process -Filter "Name='dotnet.exe'" |
    Where-Object { $_.CommandLine -like '*IsleBar.App.dll*' } |
    ForEach-Object { Stop-Process -Id $_.ProcessId -Force }
# Also kill the installed native exe — it holds the single-instance mutex, so a fresh dev launch would exit immediately (09-30).
Get-Process IsleBar.App -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 500

if ($Build) {
    # The build fails while the running app holds the DLL, so stop it first and then build
    $env:DOTNET_CLI_UI_LANGUAGE = 'en'
    & $dotnet build (Join-Path $PSScriptRoot 'src\IsleBar.App') -v q -nologo | Select-String -Pattern ' error |Build succeeded' | Select-Object -Unique
    if ($LASTEXITCODE -ne 0) { exit 1 }
}

Copy-Item (Join-Path $out 'resources.pri') $dotnetDir -Force
Get-ChildItem $out -Filter '*.xbf' | Copy-Item -Destination $dotnetDir -Force
# Fonts (ms-appx:///Assets/Fonts/...) are also looked up next to the executable
New-Item -ItemType Directory -Force (Join-Path $dotnetDir 'Assets\Fonts') | Out-Null
Copy-Item (Join-Path $out 'Assets\Fonts\*') (Join-Path $dotnetDir 'Assets\Fonts') -Force

Start-Process $dotnet -ArgumentList "`"$($dll.FullName)`"" -WorkingDirectory $out -WindowStyle Hidden
