using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsleBar.Core.Island;

/// <summary>One usage window of an agent's plan: "5h" or "7d", how much of it is used, and when it resets.</summary>
public sealed record UsageWindow(string Key, double Percent, DateTimeOffset? ResetsAt);

/// <summary>What a Codex session record says about a turn that just failed.</summary>
public sealed record CodexFailure(bool UsageLimit, StopReason Reason);

/// <summary>
/// Reads plan usage and failures from what the agents leave behind (user 10-03):
/// Claude Code passes <c>rate_limits</c> to its status line command; Codex writes <c>token_count</c> (with rate limits) and
/// <c>task_complete</c> (with an error when the turn failed) into its session record — it has no hook for a failed turn.
/// Nothing here throws: unreadable input gives null.
/// </summary>
public static class AgentUsage
{
    /// <summary>Claude Code status line input → its usage windows (Pro/Max plans only; empty for API keys).</summary>
    public static IReadOnlyList<UsageWindow> FromClaudeStatus(string? json)
    {
        var windows = new List<UsageWindow>();
        if (Parse(json) is not { } root || root["rate_limits"] is not JsonObject limits)
        {
            return windows;
        }

        foreach (var (name, key) in new[] { ("five_hour", "5h"), ("seven_day", "7d") })
        {
            if (limits[name] is JsonObject w && Number(w["used_percentage"]) is { } used)
            {
                windows.Add(new UsageWindow(key, used, Epoch(w["resets_at"])));
            }
        }

        return windows;
    }

    /// <summary>
    /// One line of a Codex session record: its usage windows if it's a rate-limit snapshot for the Codex plan, else null.
    /// The "premium" snapshots carry no windows and are skipped (they'd blank the reading).
    /// </summary>
    public static IReadOnlyList<UsageWindow>? FromCodexLine(string? line)
    {
        if (Parse(line)?["payload"] is not JsonObject payload || Text(payload["type"]) != "token_count"
            || payload["rate_limits"] is not JsonObject limits || limits["primary"] is not JsonObject)
        {
            return null;
        }

        var windows = new List<UsageWindow>();
        foreach (var (name, key) in new[] { ("primary", "5h"), ("secondary", "7d") })
        {
            if (limits[name] is JsonObject w && Number(w["used_percent"]) is { } used)
            {
                windows.Add(new UsageWindow(key, used, Epoch(w["resets_at"])));
            }
        }

        return windows;
    }

    /// <summary>One line of a Codex session record: a failed turn (task_complete with an error), else null.</summary>
    public static CodexFailure? CodexFailureFrom(string? line)
    {
        if (Parse(line)?["payload"] is not JsonObject payload || Text(payload["type"]) != "task_complete"
            || payload["error"] is not JsonObject error)
        {
            return null;
        }

        var info = (Text(error["codex_error_info"]) ?? string.Empty).ToLowerInvariant();
        if (info.Contains("usage_limit", StringComparison.Ordinal))
        {
            return new CodexFailure(true, StopReason.Other);
        }

        var reason = info switch
        {
            _ when info.Contains("auth", StringComparison.Ordinal) || info.Contains("login", StringComparison.Ordinal) => StopReason.Login,
            _ when info.Contains("credit", StringComparison.Ordinal) || info.Contains("billing", StringComparison.Ordinal)
                   || info.Contains("quota", StringComparison.Ordinal) => StopReason.Billing,
            _ when info.Contains("overload", StringComparison.Ordinal) || info.Contains("server", StringComparison.Ordinal)
                   || info.Contains("stream", StringComparison.Ordinal) || info.Contains("connection", StringComparison.Ordinal) => StopReason.Busy,
            _ => StopReason.Other,
        };
        return new CodexFailure(false, reason);
    }

    /// <summary>When a hit limit lifts: the reset of the fullest window in the last reading (the one that ran out).</summary>
    public static DateTimeOffset? ResetOfFullest(IReadOnlyList<UsageWindow>? windows)
        => windows is { Count: > 0 } ? windows.MaxBy(w => w.Percent)!.ResetsAt : null;

    /// <summary>The window to warn about (at or above <see cref="AgentNotices.WarnAtPercent"/>, below 100), fullest first.</summary>
    public static UsageWindow? ToWarnAbout(IReadOnlyList<UsageWindow>? windows)
        => windows?.Where(w => w.Percent >= AgentNotices.WarnAtPercent && w.Percent < 100).OrderByDescending(w => w.Percent).FirstOrDefault();

    /// <summary>
    /// Whether this window was already warned about (<paramref name="warnedFile"/> lists "agent:key:reset" lines); records it if not.
    /// Once per window: the same 5-hour window never warns twice. Never throws — on trouble it warns again rather than never.
    /// </summary>
    public static bool FirstWarning(string warnedFile, string agent, UsageWindow window)
    {
        var mark = $"{agent}:{window.Key}:{window.ResetsAt?.ToUnixTimeSeconds() ?? 0}";
        try
        {
            var seen = File.Exists(warnedFile) ? File.ReadAllLines(warnedFile).ToList() : [];
            if (seen.Contains(mark))
            {
                return false;
            }

            seen.Add(mark);
            Directory.CreateDirectory(Path.GetDirectoryName(warnedFile)!);
            File.WriteAllLines(warnedFile, seen.TakeLast(40));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return true;
        }
    }

    private static JsonObject? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            return JsonNode.Parse(json) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue(out string? s) ? s : null;

    private static double? Number(JsonNode? node) => node is JsonValue v && v.TryGetValue(out double d) ? d : null;

    private static DateTimeOffset? Epoch(JsonNode? node) => Number(node) is { } n ? FromUnixTime((long)n) : null;

    /// <summary>
    /// A Unix time from an agent: seconds, or milliseconds if it is too large to be seconds (FromUnixTimeSeconds threw on those,
    /// and the hook then failed — review 10-03). Null when missing or out of range.
    /// </summary>
    public static DateTimeOffset? FromUnixTime(long value)
    {
        const long MaxSeconds = 253402300799;   // 9999-12-31
        if (value <= 0)
        {
            return null;
        }

        if (value > MaxSeconds)
        {
            value /= 1000;
        }

        return value <= MaxSeconds ? DateTimeOffset.FromUnixTimeSeconds(value) : null;
    }
}
