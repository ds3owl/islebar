using System.Globalization;
using System.Text.RegularExpressions;

namespace IsleBar.Core.Island;

/// <summary>
/// Checks whether text typed into the search bar is a timer. Things like "25m", "1h 30m", "90s" (Korean unit words work too).
///
/// The rules are deliberately narrow — <b>a unit is required</b>, and the whole text must be a duration.
/// That way <b>questions</b> like "25" or "what should I do for 25 minutes?" are not hijacked as timers.
/// </summary>
public static partial class TimerParser
{
    /// <summary>Timers longer than this are rejected.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromHours(24);

    // number + unit as one chunk. Spaces/commas in between are allowed.
    [GeneratedRegex(
        @"(?<value>\d+(?:[.,]\d+)?)\s*(?<unit>시간|시|분|초|hours|hour|hrs|hr|h|minutes|minute|mins|min|m|seconds|second|secs|sec|s)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex Component();

    /// <summary>Used to check that the whole text consists only of duration chunks.</summary>
    [GeneratedRegex(@"^[\s,]+$", RegexOptions.CultureInvariant)]
    private static partial Regex OnlySeparators();

    /// <summary>
    /// True if it reads as a duration. Multiple units are added up ("1h 30m" = 90 minutes).
    /// </summary>
    public static bool TryParse(string? input, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(input))
        {
            return false;
        }

        var text = input.Trim();
        var matches = Component().Matches(text);
        if (matches.Count == 0)
        {
            return false;
        }

        // leftover characters between/around chunks mean it is not a duration (so questions are not hijacked)
        var cursor = 0;
        foreach (var match in matches.Cast<Match>())
        {
            if (match.Index > cursor && !OnlySeparators().IsMatch(text[cursor..match.Index]))
            {
                return false;
            }

            cursor = match.Index + match.Length;
        }

        if (cursor < text.Length && !OnlySeparators().IsMatch(text[cursor..]))
        {
            return false;
        }

        var total = TimeSpan.Zero;
        var seen = new HashSet<TimeUnit>();
        foreach (var match in matches.Cast<Match>())
        {
            var raw = match.Groups["value"].Value.Replace(',', '.');
            if (!double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value))
            {
                return false;
            }

            var unit = UnitOf(match.Groups["unit"].Value);
            if (!seen.Add(unit))
            {
                return false;   // something like "10m 20m" is treated as a typo
            }

            var seconds = unit switch
            {
                TimeUnit.Hour => value * 3600,
                TimeUnit.Minute => value * 60,
                _ => value,
            };
            if (double.IsNaN(seconds) || seconds > MaxDuration.TotalSeconds)
            {
                return false;   // "99999999999m" threw an OverflowException and took the bar down (review 10-03)
            }

            total += TimeSpan.FromSeconds(seconds);
        }

        // truncate to whole seconds (0.5 min → 30 s)
        total = TimeSpan.FromSeconds(Math.Round(total.TotalSeconds));
        if (total <= TimeSpan.Zero || total > MaxDuration)
        {
            return false;
        }

        duration = total;
        return true;
    }

    private enum TimeUnit
    {
        Hour,
        Minute,
        Second,
    }

    private static TimeUnit UnitOf(string unit) => unit.ToLowerInvariant() switch
    {
        "시간" or "시" or "h" or "hr" or "hrs" or "hour" or "hours" => TimeUnit.Hour,
        "초" or "s" or "sec" or "secs" or "second" or "seconds" => TimeUnit.Second,
        _ => TimeUnit.Minute,   // minutes · m · min · mins · minute · minutes (and the Korean minute unit)
    };

    /// <summary>Time remaining. If paused, the time remaining when paused. Null if not a timer (including stopwatches).</summary>
    public static TimeSpan? Remaining(ActivityState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (IsStopwatch(state))
        {
            return null;
        }

        if (state.Left is { } left)
        {
            return TimeSpan.FromSeconds(left);
        }

        return state.Due is { } due ? TimeSpan.FromSeconds(due - Seconds(now)) : null;
    }

    /// <summary>Elapsed ratio 0–1 (bar). Null if the total length is unknown.</summary>
    public static double? Fraction(ActivityState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Total is not > 0 || Remaining(state, now) is not { } left)
        {
            return null;
        }

        return Math.Clamp(1 - (left.TotalSeconds / state.Total.Value), 0, 1);
    }

    /// <summary>Pauses: records the remaining seconds and clears the end time (it does not count down while paused). No-op if already paused.
    /// For a stopwatch, records the elapsed seconds.</summary>
    public static ActivityState Pause(ActivityState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (IsStopwatch(state))
        {
            if (!state.IsPaused && Elapsed(state, now) is { } went)
            {
                state.Left = went.TotalSeconds;
            }

            return state;
        }

        if (state.IsPaused || Remaining(state, now) is not { } left)
        {
            return state;
        }

        state.Left = Math.Max(0, left.TotalSeconds);
        state.Due = null;
        return state;
    }

    /// <summary>Resumes: the end time becomes now + the remaining seconds.</summary>
    public static ActivityState Resume(ActivityState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Left is not { } left)
        {
            return state;
        }

        if (IsStopwatch(state))
        {
            state.T0 = Seconds(now) - left;   // paused time is not counted
            state.Left = null;
            return state;
        }

        state.Due = Seconds(now) + left;
        state.Left = null;
        return state;
    }

    private static double Seconds(DateTimeOffset time) => time.ToUnixTimeMilliseconds() / 1000.0;

    // ---- Stopwatch · Pomodoro (09-30: started from the right-click menu — typing into the input box risks sending it as chat; user's call) ----

    /// <summary>Stopwatch marker (<see cref="ActivityState.Stage"/>).</summary>
    public const string StopwatchStage = "stopwatch";

    /// <summary>Pomodoro marker prefix: "pomo:{round}:{focus|break}".</summary>
    public const string PomodoroPrefix = "pomo:";

    /// <summary>One Pomodoro cycle = 4 focus sessions (short breaks in between, a long break after the last).</summary>
    public const int PomodoroRounds = 4;

    public static readonly TimeSpan PomodoroFocus = TimeSpan.FromMinutes(25);
    public static readonly TimeSpan PomodoroShortBreak = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan PomodoroLongBreak = TimeSpan.FromMinutes(15);

    /// <summary>How long "done" (green border) is shown before moving to the next stage.</summary>
    public static readonly TimeSpan PomodoroHandover = TimeSpan.FromSeconds(3);

    public static bool IsStopwatch(ActivityState state) => state.Kind == ActivityKind.Timer && state.Stage == StopwatchStage;

    /// <summary>Time measured by the stopwatch (up to the pause, if paused). Null if not a stopwatch.</summary>
    public static TimeSpan? Elapsed(ActivityState state, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!IsStopwatch(state))
        {
            return null;
        }

        return state.Left is { } went
            ? TimeSpan.FromSeconds(went)
            : TimeSpan.FromSeconds(Math.Max(0, Seconds(now) - (state.T0 ?? Seconds(now))));
    }

    /// <summary>Turns a stopwatch into a state JSON (counts up from 0).</summary>
    public static ActivityState ToStopwatch(DateTimeOffset now, string label)
        => new()
        {
            RawKind = ActivityState.KindTimer,
            Stage = StopwatchStage,
            Name = label,
            State = "run",
            T0 = Seconds(now),
        };

    /// <summary>Pomodoro stage: round (1–4) and whether it is focus. Null if not a Pomodoro.</summary>
    public static (int Round, bool Focus)? PomodoroPhase(ActivityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.Stage is not { } stage || !stage.StartsWith(PomodoroPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var parts = stage[PomodoroPrefix.Length..].Split(':');
        return parts.Length == 2 && int.TryParse(parts[0], out var round)
            ? (Math.Clamp(round, 1, PomodoroRounds), parts[1] == "focus")
            : null;
    }

    /// <summary>One Pomodoro stage ("Focus 1/4", "Break 1/4") as a state JSON.</summary>
    public static ActivityState ToPomodoro(int round, bool focus, DateTimeOffset now, string focusLabel, string breakLabel, PomodoroLengths? lengths = null)
    {
        var l = lengths ?? PomodoroLengths.Default;
        var length = focus ? l.Focus : round >= PomodoroRounds ? l.LongBreak : l.ShortBreak;
        var state = ToActivity(length, now, $"{(focus ? focusLabel : breakLabel)} {round}/{PomodoroRounds}");
        state.Stage = $"{PomodoroPrefix}{round}:{(focus ? "focus" : "break")}";
        return state;
    }

    /// <summary>
    /// If this stage has ended and <see cref="PomodoroHandover"/> has passed, the next stage (focus → break → next round's focus …;
    /// after round 4's long break it ends = null). Otherwise <paramref name="state"/> unchanged.
    /// </summary>
    public static ActivityState? AdvancePomodoro(ActivityState state, DateTimeOffset now, string focusLabel, string breakLabel, PomodoroLengths? lengths = null)
    {
        if (PomodoroPhase(state) is not { } phase || state.IsPaused || state.Due is not { } due
            || Seconds(now) < due + PomodoroHandover.TotalSeconds)
        {
            return state;
        }

        if (phase.Focus)
        {
            return ToPomodoro(phase.Round, focus: false, now, focusLabel, breakLabel, lengths);
        }

        return phase.Round >= PomodoroRounds ? null : ToPomodoro(phase.Round + 1, focus: true, now, focusLabel, breakLabel, lengths);
    }

    /// <summary>Turns a timer into a state JSON.</summary>
    public static ActivityState ToActivity(TimeSpan duration, DateTimeOffset now, string? label = null)
        => new()
        {
            RawKind = ActivityState.KindTimer,
            Name = label ?? string.Empty,
            State = "run",
            T0 = now.ToUnixTimeSeconds(),
            Due = now.Add(duration).ToUnixTimeSeconds(),
            Total = (long)duration.TotalSeconds,
            Done = 0,
        };
}

/// <summary>Pomodoro stage lengths (changed in settings).</summary>
public sealed record PomodoroLengths(TimeSpan Focus, TimeSpan ShortBreak, TimeSpan LongBreak)
{
    public static PomodoroLengths Default { get; } = new(TimerParser.PomodoroFocus, TimerParser.PomodoroShortBreak, TimerParser.PomodoroLongBreak);

    public static PomodoroLengths FromMinutes(int focus, int shortBreak, int longBreak)
        => new(TimeSpan.FromMinutes(focus), TimeSpan.FromMinutes(shortBreak), TimeSpan.FromMinutes(longBreak));
}
