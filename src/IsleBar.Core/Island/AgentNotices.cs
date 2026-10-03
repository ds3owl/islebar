using System.Globalization;
using IsleBar.Core.Launch;
using IsleBar.Core.Localization;

namespace IsleBar.Core.Island;

/// <summary>
/// The pill's messages about an agent's plan and failures (user 10-03): the usage limit was hit (red, until it resets), the turn
/// stopped on another error (red, half an hour or until the next prompt), and an early warning when a usage window passes 90%.
/// Built the same way for Claude Code (its hooks and status line) and Codex (its session record), so both read alike.
/// </summary>
public static class AgentNotices
{
    /// <summary>The early warning shows once per usage window, at this share used.</summary>
    public const double WarnAtPercent = 90;

    /// <summary>How long a "stopped by an error" notice stays if nothing else replaces it.</summary>
    public static readonly TimeSpan StoppedFor = TimeSpan.FromMinutes(30);

    private const string Clock = "";      // Segoe Fluent Icons: clock ("wait until")
    private const string Warning = "";    // Segoe Fluent Icons: warning

    public static string Name(string agent) => agent == AgentKind.Codex ? "Codex" : "Claude";

    /// <summary>The plan's usage limit stopped the turn. Stays until <paramref name="resetsAt"/> (an hour if unknown).</summary>
    public static ActivityState UsageLimit(string agent, DateTimeOffset? resetsAt, LanguageStrings? text, DateTimeOffset now)
    {
        text ??= LanguageCatalog.For(LanguageCatalog.Fallback);
        return new ActivityState
        {
            RawKind = ActivityState.KindNotice,
            Agent = agent,
            Glyph = Clock,
            Title = text.UsageLimit.Replace("{a}", Name(agent), StringComparison.Ordinal),
            Msg = resetsAt is { } r ? text.ResetsAt.Replace("{}", Time(r), StringComparison.Ordinal) : null,
            State = "error",
            T0 = Seconds(now),
            Due = Seconds(resetsAt ?? now.AddHours(1)),
        };
    }

    /// <summary>The turn ended on an error other than the usage limit. <paramref name="reason"/>: login, billing, busy or anything else.</summary>
    public static ActivityState Stopped(string agent, StopReason reason, LanguageStrings? text, DateTimeOffset now)
    {
        text ??= LanguageCatalog.For(LanguageCatalog.Fallback);
        return new ActivityState
        {
            RawKind = ActivityState.KindNotice,
            Agent = agent,
            Glyph = Warning,
            Title = text.AgentStopped.Replace("{a}", Name(agent), StringComparison.Ordinal),
            Msg = reason switch
            {
                StopReason.Login => text.StopLogin,
                StopReason.Billing => text.StopBilling,
                StopReason.Busy => text.StopBusy,
                _ => text.StopError,
            },
            State = "error",
            T0 = Seconds(now),
            Due = Seconds(now + StoppedFor),
        };
    }

    /// <summary>A usage window passed <see cref="WarnAtPercent"/>: a plain notice (no border), shown briefly.</summary>
    public static ActivityState UsageHigh(string agent, double percent, DateTimeOffset? resetsAt, LanguageStrings? text, DateTimeOffset now)
    {
        text ??= LanguageCatalog.For(LanguageCatalog.Fallback);
        return new ActivityState
        {
            RawKind = ActivityState.KindNotice,
            Agent = agent,
            Glyph = Clock,
            Title = text.UsageHigh.Replace("{a}", Name(agent), StringComparison.Ordinal)
                                   .Replace("{p}", Math.Floor(percent).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal),
            Msg = resetsAt is { } r ? text.ResetsAt.Replace("{}", Time(r), StringComparison.Ordinal) : null,
            State = "run",
            T0 = Seconds(now),
        };
    }

    /// <summary>Claude Code's StopFailure error types (hooks reference) → why it stopped. Null for the usage limit (its own notice).</summary>
    public static StopReason? ClaudeReason(string? errorType) => errorType switch
    {
        "rate_limit" => null,
        "authentication_failed" or "oauth_org_not_allowed" or "cloud_credential_error" => StopReason.Login,
        "billing_error" or "account_on_hold" => StopReason.Billing,
        "overloaded" or "server_error" => StopReason.Busy,
        _ => StopReason.Other,
    };

    /// <summary>Local clock time, "HH:mm" — with the date in front when it isn't today ("10-04 05:42").</summary>
    public static string Time(DateTimeOffset at)
    {
        var local = at.ToLocalTime();
        return local.Date == DateTimeOffset.Now.Date
            ? local.ToString("HH:mm", CultureInfo.InvariantCulture)
            : local.ToString("MM-dd HH:mm", CultureInfo.InvariantCulture);
    }

    private static double Seconds(DateTimeOffset at) => at.ToUnixTimeMilliseconds() / 1000.0;
}

public enum StopReason
{
    Login,
    Billing,
    Busy,
    Other,
}
