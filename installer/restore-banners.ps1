# Uninstall: gives every app back its Windows notification banners. IsleBar's "show notifications only in the bar" option turns
# them off per app and keeps the original values in toast_banners.json; the app restores them when it quits normally, but the
# uninstaller stops it hard, so do the same restore here. (null in the ledger = the value didn't exist → remove it again.)
# Like the app, only an app still switched off (0) is touched — a choice the user made since is left alone.
param([string]$Ledger = (Join-Path $env:LOCALAPPDATA 'IsleBar\toast_banners.json'))

if (-not (Test-Path $Ledger)) { return }
$root = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Notifications\Settings'
try { $data = Get-Content $Ledger -Raw -Encoding UTF8 | ConvertFrom-Json } catch { $data = $null }
$count = 0
if ($data) {
    foreach ($e in $data.PSObject.Properties) {
        $key = Join-Path $root $e.Name
        if (-not (Test-Path -LiteralPath $key)) { continue }
        $now = (Get-ItemProperty -LiteralPath $key -Name ShowBanner -ErrorAction SilentlyContinue).ShowBanner
        if ($now -ne 0) { continue }
        if ($null -eq $e.Value) { Remove-ItemProperty -LiteralPath $key -Name ShowBanner -ErrorAction SilentlyContinue }
        else { Set-ItemProperty -LiteralPath $key -Name ShowBanner -Value ([int]$e.Value) -Type DWord }
        $count++
    }
}
Remove-Item $Ledger -Force
"banners restored: $count app(s)"
