using System.Globalization;
using IsleBar.Core.Localization;

namespace IsleBar.Core.SystemWatch;

/// <summary>
/// Builds notice text. Fills the translation placeholders (<c>{}</c> name, <c>{p}</c> percent, <c>{m}</c> minutes, <c>{h}</c> hours,
/// <c>{n}</c> count). Leading and trailing parts are always joined with " · " (like the island's other text).
/// </summary>
public static class NoticeText
{
    /// <summary>Joiner inside titles. "Charging · 78%".</summary>
    public const string Separator = " · ";

    /// <summary>Dots shown instead of copied content that looks like a password.</summary>
    public const string Masked = "••••••";

    public static string Join(string head, string? tail)
        => string.IsNullOrWhiteSpace(tail) ? head : head + Separator + tail;

    public static string Charging(LanguageStrings s, int percent) => Percent(s.NoticeCharging, percent);

    public static string OnBattery(LanguageStrings s, int percent) => Percent(s.NoticeOnBattery, percent);

    public static string BatteryLevel(LanguageStrings s, int percent) => Percent(s.NoticeBatteryLevel, percent);

    /// <summary>"Battery 20% · about 45 min". Just the first part if the time remaining is unknown.</summary>
    public static string BatteryLow(LanguageStrings s, int percent, TimeSpan? remaining)
        => Join(BatteryLevel(s, percent), Remaining(s, remaining));

    /// <summary>
    /// "about 45 min" · "about 1 h 20 min". Null if unknown or nonsensical (under 1 minute, over a day) —
    /// Windows gives a very large value when it does not know yet, e.g. right after unplugging.
    /// </summary>
    public static string? Remaining(LanguageStrings s, TimeSpan? remaining)
    {
        if (remaining is not { } t || t < TimeSpan.FromMinutes(1) || t > TimeSpan.FromHours(24))
        {
            return null;
        }

        var minutes = (int)t.TotalMinutes;
        return minutes < 60
            ? Fill(s.NoticeAboutMin, "{m}", minutes)
            : Fill(Fill(s.NoticeAboutHourMin, "{h}", minutes / 60), "{m}", minutes % 60);
    }

    public static string Connected(LanguageStrings s, string name) => Name(s.NoticeConnected, name);

    public static string Disconnected(LanguageStrings s, string name) => Name(s.NoticeDisconnected, name);

    public static string Copied(LanguageStrings s, string preview) => Name(s.NoticeCopied, preview);

    /// <summary>"File copied" (1) · "3 files copied".</summary>
    public static string CopiedFiles(LanguageStrings s, int count)
        => count <= 1 ? s.NoticeCopiedFile : Fill(s.NoticeCopiedFiles, "{n}", count);

    /// <summary>"CPU 96% · chrome". Just the first part if the program name is unknown.</summary>
    public static string Cpu(LanguageStrings s, int percent, string? topProcess)
        => Join(Percent(s.NoticeCpu, percent), topProcess);

    public static string Memory(LanguageStrings s, int percent) => Percent(s.NoticeMemory, percent);

    /// <summary>"In 10 min · Team meeting".</summary>
    public static string EventSoon(LanguageStrings s, int minutes, string summary)
        => Join(Fill(s.NoticeEventSoon, "{m}", Math.Max(1, minutes)), summary);

    /// <summary>"Team meeting · Now".</summary>
    public static string EventNow(LanguageStrings s, string summary) => Join(summary, s.NoticeEventNow);

    /// <summary>"Microphone in use · Zoom".</summary>
    public static string InUse(LanguageStrings s, bool camera, string? app)
        => Join(camera ? s.NoticeCameraInUse : s.NoticeMicInUse, app);

    private static string Percent(string template, int percent) => Fill(template, "{p}", Math.Clamp(percent, 0, 100));

    private static string Name(string template, string name)
        => template.Replace("{}", name ?? string.Empty, StringComparison.Ordinal);

    private static string Fill(string template, string key, int value)
        => template.Replace(key, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
