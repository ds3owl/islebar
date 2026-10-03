# Connects Claude Code (and Codex, if it's set up) to IsleBar: every hook calls `islebar.exe hook`, which updates the pill.
# Run by the installer (optional task "Connect Claude Code notifications") and by the uninstaller (-Revert).
#
#   hooks.ps1 -Apply     add IsleBar's hooks (others are left alone; settings.json is backed up first)
#   hooks.ps1 -Revert    remove only IsleBar's hooks again
#   -SettingsPath / -CodexConfig point at other files (used for testing)
param(
    [switch]$Apply,
    [switch]$Revert,
    [string]$SettingsPath = (Join-Path $env:USERPROFILE '.claude\settings.json'),
    [string]$CodexConfig = (Join-Path ($(if ($env:CODEX_HOME) { $env:CODEX_HOME } else { Join-Path $env:USERPROFILE '.codex' })) 'config.toml'),
    [string]$Exe = (Join-Path $env:LOCALAPPDATA 'IsleBar\bin\islebar.exe')
)

$ErrorActionPreference = 'Stop'
$command = '"' + $Exe + '" hook'
# Claude Code hands its plan usage only to a status line command — ours shows "5h 42% | 7d 17%" there and lets the bar warn at
# 90% (10-03). Added only when no status line is set (someone's own is never replaced); removed again only if it is ours.
$statusCommand = '"' + $Exe + '" statusline'
function Is-OurStatus($s) { $s -and $s.command -like '*\islebar.exe*statusline*' }
# Notification = permission/question (orange) · Stop = answer finished (green) · SessionEnd = clear ·
# UserPromptSubmit = working · PostToolUse/PostToolUseFailure = a question was answered → back to working · StopFailure (rate_limit) = usage limit (red, until it resets)
$events = @('Notification', 'Stop', 'StopFailure', 'SessionEnd', 'UserPromptSubmit', 'PostToolUse', 'PostToolUseFailure')

# Setup build: ...\IsleBar\bin\islebar.exe · Store build: ...\Microsoft\WindowsApps\islebar.exe (app execution alias) — both are ours
function Is-Ours($h) { $h.command -like '*\islebar.exe*hook*' }

# Takes our hooks out of a "hooks" object; groups we don't recognise and everyone else's hooks stay as they are.
function Remove-OurHooks($hooks) {
    foreach ($e in @($hooks.PSObject.Properties | ForEach-Object { $_.Name } | Where-Object { $_ })) {
        $kept = @()
        foreach ($g in @($hooks.$e)) {
            if ($null -eq $g) { continue }
            if (-not $g.PSObject.Properties['hooks']) { $kept += $g; continue }
            $hs = @(@($g.hooks) | Where-Object { $_ -and -not (Is-Ours $_) })
            if ($hs.Count -gt 0) { $g.hooks = $hs; $kept += $g }
        }
        if ($kept.Count -gt 0) { $hooks.$e = $kept } else { $hooks.PSObject.Properties.Remove($e) }
    }
}

function Save-Json($obj, $path) {
    $text = $obj | ConvertTo-Json -Depth 32
    [System.IO.File]::WriteAllText($path, $text, (New-Object System.Text.UTF8Encoding($false)))
}

$failed = $false

# ---- Claude Code ----
try {
if (Test-Path (Split-Path $SettingsPath -Parent)) {
    $json = if (Test-Path $SettingsPath) { Get-Content $SettingsPath -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
    if ($null -eq $json) { $json = [pscustomobject]@{} }
    if (-not $json.PSObject.Properties['hooks']) { $json | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) }

    if ($Apply) {
        # keep the first backup — a reinstall must not replace the pre-IsleBar copy with one that already has our hooks
        if ((Test-Path $SettingsPath) -and -not (Test-Path "$SettingsPath.islebar-backup")) { Copy-Item $SettingsPath "$SettingsPath.islebar-backup" }
        foreach ($e in $events) {
            $groups = @($json.hooks.$e) | Where-Object { $_ }
            $has = $false
            # already there → bring the command up to date (a reinstall must replace an older build's command, not keep it)
            # a time limit like the Codex hooks: Claude Code's default would wait a minute if the hook ever hung (reinstall test 10-03)
            foreach ($g in $groups) { foreach ($h in @($g.hooks)) { if (Is-Ours $h) { $has = $true; $h | Add-Member -NotePropertyName command -NotePropertyValue $command -Force; $h | Add-Member -NotePropertyName timeout -NotePropertyValue 10 -Force } } }
            if ($has) { continue }
            $entry = [pscustomobject]@{ hooks = @([pscustomobject]@{ type = 'command'; command = $command; timeout = 10 }) }
            if ($json.hooks.PSObject.Properties[$e]) { $json.hooks.$e = @($groups) + $entry }
            else { $json.hooks | Add-Member -NotePropertyName $e -NotePropertyValue @($entry) }
        }
        if (-not $json.PSObject.Properties['statusLine']) {
            $json | Add-Member -NotePropertyName statusLine -NotePropertyValue ([pscustomobject]@{ type = 'command'; command = $statusCommand })
        }
        elseif (Is-OurStatus $json.statusLine) { $json.statusLine.command = $statusCommand }
        Save-Json $json $SettingsPath
        "claude: hooks connected"
    }
    elseif ($Revert -and (Test-Path $SettingsPath)) {
        Remove-OurHooks $json.hooks
        if ($json.PSObject.Properties['statusLine'] -and (Is-OurStatus $json.statusLine)) { $json.PSObject.Properties.Remove('statusLine') }
        Save-Json $json $SettingsPath
        "claude: hooks removed"
    }
}
}
catch { $failed = $true; "claude: left unchanged — $($_.Exception.Message)" }

# ---- Codex (0.15x+): lifecycle hooks in hooks.json next to config.toml — working · needs an answer · done, like Claude.
# Codex asks the person to trust new hooks once (/hooks in Codex). The legacy notify line (done only) is removed so a finished
# turn doesn't ring twice; -Revert takes our hooks out again and leaves everyone else's.
$codexDir = Split-Path $CodexConfig -Parent
$codexHooks = Join-Path $codexDir 'hooks.json'
# Codex on Windows runs hook commands through PowerShell: a quoted path needs the call operator (&), and PowerShell doesn't
# hand its stdin to a native program on its own — read it and pipe it in, as UTF-8 so Korean folder names survive (10-01).
# Read stdin as UTF-8 too: PowerShell decodes it with the console code page (949 on Korean Windows), which mangled Korean
# prompts/folders and broke the JSON — the pill silently stayed dark (code review 10-01).
$codexCommand = '[Console]::InputEncoding = [Text.UTF8Encoding]::new($false); $d = [Console]::In.ReadToEnd(); $OutputEncoding = [Text.UTF8Encoding]::new($false); $d | & "' + $Exe + '" hook --agent codex'
$codexEvents = @('UserPromptSubmit', 'PermissionRequest', 'PostToolUse', 'Stop', 'SessionEnd')
try {
if (Test-Path $codexDir) {
    $hj = if (Test-Path $codexHooks) { Get-Content $codexHooks -Raw -Encoding UTF8 | ConvertFrom-Json } else { [pscustomobject]@{} }
    if ($null -eq $hj) { $hj = [pscustomobject]@{} }
    if (-not $hj.PSObject.Properties['hooks']) { $hj | Add-Member -NotePropertyName hooks -NotePropertyValue ([pscustomobject]@{}) }
    if ($Apply) {
        foreach ($e in $codexEvents) {
            $groups = @($hj.hooks.$e) | Where-Object { $_ }
            $has = $false
            # Codex caps SessionEnd hooks at 3 s and prints a warning on every start if a hook asks for more
            $timeout = if ($e -eq 'SessionEnd') { 3 } else { 10 }
            # already there → update it in place: the first build's command read stdin in the console code page, and a
            # reinstall that kept it left Korean prompts dark (found reinstalling 10-01). Codex asks to trust the changed hook once.
            foreach ($g in $groups) { foreach ($h in @($g.hooks)) { if (Is-Ours $h) { $has = $true; $h | Add-Member -NotePropertyName command -NotePropertyValue $codexCommand -Force; $h | Add-Member -NotePropertyName timeout -NotePropertyValue $timeout -Force } } }
            if ($has) { continue }
            $entry = [pscustomobject]@{ hooks = @([pscustomobject]@{ type = 'command'; command = $codexCommand; timeout = $timeout }) }
            if ($hj.hooks.PSObject.Properties[$e]) { $hj.hooks.$e = @($groups) + $entry }
            else { $hj.hooks | Add-Member -NotePropertyName $e -NotePropertyValue @($entry) }
        }
        Save-Json $hj $codexHooks
        "codex: hooks connected (trust them once with /hooks in Codex)"
    }
    elseif ($Revert -and (Test-Path $codexHooks)) {
        Remove-OurHooks $hj.hooks
        if (@($hj.hooks.PSObject.Properties).Count -eq 0 -and @($hj.PSObject.Properties).Count -eq 1) { Remove-Item $codexHooks -Force }
        else { Save-Json $hj $codexHooks }
        "codex: hooks removed"
    }
}
}
catch { $failed = $true; "codex: left unchanged — $($_.Exception.Message)" }

# the legacy notify line (ours only)
if (Test-Path $CodexConfig) {
    $lines = [System.Collections.Generic.List[string]](Get-Content $CodexConfig -Encoding UTF8)
    $idx = -1; for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match '^\s*notify\s*=' -and $lines[$i].Replace('\\', '\') -like '*\islebar.exe*') { $idx = $i; break } }   # TOML basic strings write \ as \\
    if ($idx -ge 0 -and ($Apply -or $Revert)) {
        $lines.RemoveAt($idx)
        [System.IO.File]::WriteAllLines($CodexConfig, $lines, (New-Object System.Text.UTF8Encoding($false)))
        "codex: legacy notify removed"
    }
}

if ($failed) { exit 1 }
