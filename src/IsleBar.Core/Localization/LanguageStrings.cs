using System.Globalization;

namespace IsleBar.Core.Localization;

/// <summary>
/// All UI strings for one language. A straight port of the Python version's <c>_T</c> dictionary, so
/// placeholders keep the Python shape (<c>{}</c>, <c>{m}</c>, <c>{s:02d}</c>) —
/// to allow side-by-side comparison with the original. Formatting goes only through the Format* methods below.
/// </summary>
public sealed class LanguageStrings
{
    public required string Finishing { get; init; }
    public required string Options { get; init; }
    public required string Settings { get; init; }
    public required string Quit { get; init; }

    /// <summary>Launch failure notice. The error message goes in <c>{}</c> — use <see cref="FormatLaunchFail"/>.</summary>
    public required string LaunchFail { get; init; }

    public required string NoSdk { get; init; }
    public required string EsOff { get; init; }
    public required string EsFail { get; init; }
    public required string NoResult { get; init; }
    public required string Done { get; init; }
    public required string Fail { get; init; }
    public required string Prep { get; init; }

    /// <summary>1 minute or more. <c>{m}</c> minutes, <c>{s:02d}</c> seconds (two digits) — use <see cref="FormatDuration"/>.</summary>
    public required string MinSec { get; init; }

    /// <summary>Under 1 minute. <c>{s}</c> seconds — use <see cref="FormatDuration"/>.</summary>
    public required string Sec { get; init; }

    public required string Lang { get; init; }
    public required string Auto { get; init; }
    public required string Folder { get; init; }
    public required string FolderChange { get; init; }
    public required string FolderHome { get; init; }
    public required string Files { get; init; }

    public required OptionText Session { get; init; }
    public required OptionText Model { get; init; }
    public required OptionText Effort { get; init; }
    public required OptionText Perm { get; init; }

    /// <summary>Remote control is an on/off toggle, so it has no choices.</summary>
    public required string Rc { get; init; }

    /// <summary>Title of "Agent" (Claude / Codex picker) in the settings window.</summary>
    public required string Agent { get; init; }

    /// <summary>"Dismiss" in the right-click menu (island item — cancel a timer, etc.).</summary>
    public required string Dismiss { get; init; }

    /// <summary>Title of "Web search engine" in the settings window.</summary>
    public required string WebEngine { get; init; }

    /// <summary>Title of "Tab order" in the settings window.</summary>
    public required string TabOrder { get; init; }

    /// <summary>Mode name "Web search" (Tab order list).</summary>
    public required string WebSearch { get; init; }

    /// <summary>Timer label on the island ("Timer 18:42" — like "Pomodoro 18:42" in the preview).</summary>
    public required string Timer { get; init; }

    // ---- System notices (09-30). Placeholders: {p} percent · {m} minutes · {h} hours · {n} count · {} name.
    //      Formatted only via SystemWatch.NoticeText. ----

    /// <summary>When the charger is plugged in: "Charging · {p}%".</summary>
    public required string NoticeCharging { get; init; }

    /// <summary>When the charger is unplugged: "On battery · {p}%".</summary>
    public required string NoticeOnBattery { get; init; }

    /// <summary>Low battery / Bluetooth device battery: "Battery {p}%".</summary>
    public required string NoticeBatteryLevel { get; init; }

    /// <summary>Time remaining (under 1 hour): "about {m} min".</summary>
    public required string NoticeAboutMin { get; init; }

    /// <summary>Time remaining (1 hour or more): "about {h} h {m} min".</summary>
    public required string NoticeAboutHourMin { get; init; }

    /// <summary>Bluetooth "{} · Connected" — {} = device name.</summary>
    public required string NoticeConnected { get; init; }

    /// <summary>Bluetooth "{} · Disconnected".</summary>
    public required string NoticeDisconnected { get; init; }

    public required string NoticeOffline { get; init; }
    public required string NoticeOnline { get; init; }
    public required string NoticeFocus { get; init; }

    /// <summary>"Copied · {}" — {} = start of the copied text (masked if it looks like a password).</summary>
    public required string NoticeCopied { get; init; }

    public required string NoticeCopiedImage { get; init; }

    /// <summary>A single file.</summary>
    public required string NoticeCopiedFile { get; init; }

    /// <summary>Several files: "{n} files copied".</summary>
    public required string NoticeCopiedFiles { get; init; }

    public required string NoticeCpu { get; init; }
    public required string NoticeMemory { get; init; }

    /// <summary>Calendar "In {m} min" (followed by " · event name").</summary>
    public required string NoticeEventSoon { get; init; }

    /// <summary>Calendar event starting: "Now" (preceded by "event name · ").</summary>
    public required string NoticeEventNow { get; init; }

    public required string NoticeMicInUse { get; init; }
    public required string NoticeCameraInUse { get; init; }

    // ---- System notice on/off toggles in the settings window ----

    /// <summary>Group title "Island notices" in the settings window.</summary>
    public required string NotifySection { get; init; }

    public required string NotifyPower { get; init; }
    public required string NotifyBluetooth { get; init; }
    public required string NotifyNetwork { get; init; }
    public required string NotifyFocus { get; init; }
    public required string NotifyClipboard { get; init; }
    public required string NotifyLoad { get; init; }
    public required string NotifyPrivacy { get; init; }

    /// <summary>Setting: pull in Windows notifications (optional, off by default).</summary>
    public required string NotifyToasts { get; init; }

    /// <summary>Settings toggle: hide Windows' own notification pop-ups, show them only on the bar.</summary>
    public required string HideToastBanners { get; init; }

    /// <summary>Settings: opt-in crash reports — says plainly what is (and isn't) sent (10-01).</summary>
    public required string CrashReports { get; init; }

    /// <summary>Settings: daily check for a newer version (10-01).</summary>
    public required string CheckUpdates { get; init; }

    /// <summary>Card text when a newer IsleBar is out: "Update available — click to install".</summary>
    public required string UpdateAvailable { get; init; }

    /// <summary>Right-click menu item while an update is available: "Update now".</summary>
    public required string UpdateMenu { get; init; }

    /// <summary>Right-click menu: open the Windows notification centre (10-03 — the full notification list lives there).</summary>
    public required string AllNotifications { get; init; }

    /// <summary>An agent hit its plan's usage limit; {a} = agent name.</summary>
    public required string UsageLimit { get; init; }

    /// <summary>Early warning at 90% of a usage window; {a} = agent, {p} = percent.</summary>
    public required string UsageHigh { get; init; }

    /// <summary>An agent's turn ended on an error (not the usage limit); {a} = agent.</summary>
    public required string AgentStopped { get; init; }

    /// <summary>Why it stopped: authentication failed.</summary>
    public required string StopLogin { get; init; }

    /// <summary>Why it stopped: billing / account / plan problem.</summary>
    public required string StopBilling { get; init; }

    /// <summary>Why it stopped: overloaded servers.</summary>
    public required string StopBusy { get; init; }

    /// <summary>Why it stopped: any other error.</summary>
    public required string StopError { get; init; }

    /// <summary>Settings switch: no chime while a full-screen app (game, video) is in front.</summary>
    public required string MuteChimeFullscreen { get; init; }

    /// <summary>When the usage limit lifts; {} = local time (HH:mm).</summary>
    public required string ResetsAt { get; init; }

    /// <summary>Card when a one-click update could not be downloaded or checked: "Update failed — click to download it yourself".</summary>
    public required string UpdateFailed { get; init; }

    /// <summary>Settings: connect / disconnect Claude Code and Codex (writes or removes IsleBar's hooks).</summary>
    public required string ConnectAgents { get; init; }

    /// <summary>Store build's first-start card: "Show what Claude Code / Codex is doing here — click to connect".</summary>
    public required string ConnectAgentsCard { get; init; }

    /// <summary>Title of the ICS URL input for calendar notices.</summary>
    public required string CalendarIcs { get; init; }

    /// <summary>Hint for the ICS input (shown when empty).</summary>
    public required string CalendarIcsHint { get; init; }

    /// <summary>Command that receives dropped files (settings window).</summary>
    public required string DropCommand { get; init; }

    /// <summary>Drop command description.</summary>
    public required string DropCommandHint { get; init; }

    /// <summary>
    /// Pill text while a file is dragged over it and there's no drop command ("attach to prompt"). With a drop command set, the pill
    /// shows only a file icon and the name — the command can be anything, so no wording claims what it does (user 10-01).
    /// </summary>
    public required string DropToAttach { get; init; }

    /// <summary>Input-box question when a timer is started while another one runs ("Replace timer? Enter / Esc").</summary>
    public required string ReplaceTimerAsk { get; init; }

    /// <summary>"Appearance" section in the settings window.</summary>
    public required string StyleSection { get; init; }

    // Advanced/power options (10-01). Not required — an English default keeps every language compiling; the Korean catalog
    // overrides these. (A small power-options surface, so other languages fall back to English until translated.)

    /// <summary>"Advanced" section heading in the settings window.</summary>
    public string AdvancedSection { get; init; } = "Advanced";

    /// <summary>Style: highlight line + faint border.</summary>
    public required string StyleGlass { get; init; }

    /// <summary>Pomodoro (right-click menu, island label).</summary>
    public required string Pomodoro { get; init; }

    /// <summary>Stopwatch (right-click menu, island label).</summary>
    public required string Stopwatch { get; init; }

    /// <summary>Label for the Pomodoro focus stage.</summary>
    public required string Focus { get; init; }

    /// <summary>Label for the Pomodoro break stage.</summary>
    public required string Break { get; init; }

    /// <summary>"{0} min" — timer length in the right-click menu.</summary>
    public required string Minutes { get; init; }

    /// <summary>Settings window: Pomodoro lengths row.</summary>
    public required string PomodoroMinutes { get; init; }

    /// <summary>Settings window: quick timer list.</summary>
    public required string QuickTimersLabel { get; init; }

    /// <summary>Settings window: make the real search box transparent inside Explorer (native).</summary>
    public required string NativeSearchBox { get; init; }

    /// <summary>Settings: music bars follow the actual sound.</summary>
    public required string AudioBars { get; init; }

    /// <summary>Island: "Claude working · session".</summary>
    public required string Working { get; init; }

    /// <summary>Pill word for an agent waiting on a permission prompt or question (orange): "Claude needs you · name".</summary>
    public required string NeedsAnswer { get; init; }

    /// <summary>"done" after an agent's name ("Claude done") — lower-case like <see cref="Working"/>, unlike the stand-alone <see cref="Done"/> (10-01).</summary>
    public required string AgentDone { get; init; }

    public string FormatLaunchFail(string message)
        => LaunchFail.Replace("{}", message ?? string.Empty, StringComparison.Ordinal);

    /// <summary>
    /// Remaining/elapsed time display. <see cref="MinSec"/> for 60 seconds or more, otherwise <see cref="Sec"/>.
    /// Negative values are treated as 0.
    /// </summary>
    public string FormatDuration(double seconds)
    {
        var total = seconds > 0 && !double.IsNaN(seconds) ? (long)seconds : 0;
        if (total < 60)
        {
            return Sec.Replace("{s}", total.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        }

        var minutes = total / 60;
        var rest = total % 60;
        return MinSec
            .Replace("{m}", minutes.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal)
            .Replace("{s:02d}", rest.ToString("00", CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    /// <summary>
    /// Model option matching the current alias list. Only the first choice (the translated "Default") is kept; the rest use
    /// each alias with its first letter capitalized — so the buttons follow automatically as aliases are added or removed.
    /// </summary>
    public OptionText ModelFor(IReadOnlyList<string> models)
    {
        ArgumentNullException.ThrowIfNull(models);
        var choices = new List<string>(models.Count + 1) { Model.Choices[0] };
        foreach (var alias in models)
        {
            choices.Add(Capitalize(alias));
        }

        return new OptionText(Model.Title, choices);
    }

    /// <summary>Same as Python's <c>str.capitalize()</c>: first letter upper-case, the rest lower-case.</summary>
    internal static string Capitalize(string value)
        => string.IsNullOrEmpty(value)
            ? value
            : char.ToUpperInvariant(value[0]) + value[1..].ToLowerInvariant();

    /// <summary>Finds a choice option's strings by option name. rc has no choices, so null.</summary>
    public OptionText? OptionFor(string option) => option switch
    {
        Configuration.LaunchOptionDefs.Session => Session,
        Configuration.LaunchOptionDefs.Model => Model,
        Configuration.LaunchOptionDefs.Effort => Effort,
        Configuration.LaunchOptionDefs.Perm => Perm,
        _ => null,
    };

    /// <summary>Option title used in the pin menu (including rc).</summary>
    public string TitleFor(string option)
        => option == Configuration.LaunchOptionDefs.Rc ? Rc : OptionFor(option)?.Title ?? option;
}
