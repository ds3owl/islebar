# Builds the Microsoft Store package: dist\IsleBar-<version>.msix (unsigned — the Store signs it on upload).
#
#   .\scripts\build-msix.ps1 -IdentityName 12345ds3owl.IsleBar -Publisher "CN=XXXXXXXX-XXXX-..." [-Version 0.1.0]
#       Name and Publisher: Partner Center › your app › Product identity (after reserving the name "IsleBar").
#   .\scripts\build-msix.ps1 ... -NoNativeMode
#       leaves out the optional Explorer module ("hide the real search box"); use it only if certification refuses that
#       feature — it is off by default, uses the documented XAML diagnostics API (as TranslucentTB in the Store does) and
#       only changes the search box's opacity, so the first submission keeps it.
#   .\scripts\build-msix.ps1 -Test
#       a locally signed test package (self-signed "CN=IsleBar Test"); installing it needs Developer Mode or the test
#       certificate in Trusted People.
param(
    [string]$IdentityName = 'ds3owl.IsleBar.Test',
    [string]$Publisher = 'CN=IsleBar Test',
    [string]$PublisherDisplayName = 'ds3owl',
    [string]$Version = '0.1.0',
    [switch]$Test,
    [switch]$NoNativeMode
)

$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent $PSScriptRoot
$dotnet = Join-Path $env:USERPROFILE '.dotnet\dotnet.exe'
if (-not (Test-Path $dotnet)) { $dotnet = 'dotnet' }
$kit = Get-ChildItem "${env:ProgramFiles(x86)}\Windows Kits\10\bin\*\x64\makeappx.exe" | Sort-Object FullName -Descending | Select-Object -First 1
if (-not $kit) { throw 'makeappx.exe not found — install the Windows 10/11 SDK' }
$makeappx = $kit.FullName
$signtool = Join-Path $kit.DirectoryName 'signtool.exe'

$layout = Join-Path $repo 'build\msix'
Remove-Item -Recurse -Force $layout -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $layout | Out-Null

# the app (self-contained, like the setup build) and the hook CLI in its own folder (cli\islebar.exe, behind the alias)
& $dotnet publish (Join-Path $repo 'src\IsleBar.App') -c Release -r win-x64 --self-contained true -o $layout -nologo -v q "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'app publish failed' }
if ($NoNativeMode) { Remove-Item -Recurse -Force (Join-Path $layout 'native') -ErrorAction SilentlyContinue }   # settings then hide the switch
& $dotnet publish (Join-Path $repo 'src\IsleBar.Cli') -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true -o (Join-Path $layout 'cli') -nologo -v q "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'cli publish failed' }

# the hooks script the app runs to connect Claude Code / Codex (no setup program in the Store build), licences, logos
New-Item -ItemType Directory -Force (Join-Path $layout 'setup') | Out-Null
Copy-Item (Join-Path $repo 'installer\hooks.ps1') (Join-Path $layout 'setup')
Copy-Item (Join-Path $repo 'LICENSE') (Join-Path $layout 'LICENSE.txt')
Copy-Item (Join-Path $repo 'THIRD-PARTY-NOTICES.md') $layout
Copy-Item -Recurse (Join-Path $repo 'licenses') (Join-Path $layout 'licenses')
New-Item -ItemType Directory -Force (Join-Path $layout 'Assets') | Out-Null
Copy-Item (Join-Path $repo 'msix\Assets\*.png') (Join-Path $layout 'Assets') -Exclude 'StoreLogo-300.png'

$v = [version]$Version
$manifest = (Get-Content (Join-Path $repo 'msix\AppxManifest.template.xml') -Raw -Encoding UTF8).
    Replace('{{IdentityName}}', $IdentityName).Replace('{{Publisher}}', $Publisher).
    Replace('{{PublisherDisplayName}}', $PublisherDisplayName).Replace('{{Version}}', "$($v.Major).$($v.Minor).$([Math]::Max(0, $v.Build)).0")
[IO.File]::WriteAllText((Join-Path $layout 'AppxManifest.xml'), $manifest, (New-Object Text.UTF8Encoding($false)))

New-Item -ItemType Directory -Force (Join-Path $repo 'dist') | Out-Null
$out = Join-Path $repo "dist\IsleBar-$Version$(if ($Test) { '-test' }).msix"
& $makeappx pack /d $layout /p $out /o | Select-Object -Last 3
if ($LASTEXITCODE -ne 0) { throw 'makeappx failed' }

if ($Test) {
    $pfx = Join-Path $repo 'build\islebar-test.pfx'
    if (-not (Test-Path $pfx)) {
        $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $Publisher -CertStoreLocation Cert:\CurrentUser\My `
            -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3', '2.5.29.19={text}')
        $pwd = ConvertTo-SecureString -String 'islebar-test' -Force -AsPlainText
        Export-PfxCertificate -Cert $cert -FilePath $pfx -Password $pwd | Out-Null
        Export-Certificate -Cert $cert -FilePath (Join-Path $repo 'build\islebar-test.cer') | Out-Null
    }
    & $signtool sign /fd SHA256 /f $pfx /p 'islebar-test' $out | Select-Object -Last 1
}
Get-Item $out
