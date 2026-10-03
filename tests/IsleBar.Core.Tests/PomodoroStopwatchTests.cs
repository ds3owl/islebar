using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class PomodoroStopwatchTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 30, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Stopwatch_counts_up_from_zero_and_stops_when_paused()
    {
        var sw = TimerParser.ToStopwatch(Now, "스톱워치");
        Assert.True(TimerParser.IsStopwatch(sw));
        Assert.Null(TimerParser.Remaining(sw, Now));
        Assert.Equal(TimeSpan.FromSeconds(90), TimerParser.Elapsed(sw, Now.AddSeconds(90)));

        TimerParser.Pause(sw, Now.AddSeconds(90));
        Assert.Equal(TimeSpan.FromSeconds(90), TimerParser.Elapsed(sw, Now.AddSeconds(500)));

        TimerParser.Resume(sw, Now.AddSeconds(500));
        Assert.Equal(TimeSpan.FromSeconds(100), TimerParser.Elapsed(sw, Now.AddSeconds(510)));
    }

    [Fact]
    public void Stopwatch_stays_alive_even_if_file_unchanged_for_long()
        => Assert.True(ActivityStore.IsAlive(TimerParser.ToStopwatch(Now, "sw"), Now, Now.AddHours(3)));

    [Fact]
    public void Pomodoro_runs_four_focus_break_cycles_then_ends()
    {
        var state = TimerParser.ToPomodoro(1, focus: true, Now, "집중", "휴식");
        Assert.Equal("집중 1/4", state.Name);
        var t = Now;
        var names = new List<string>();
        while (state is not null)
        {
            names.Add(state.Name!);
            t = t.Add(TimeSpan.FromSeconds(state.Total!.Value)).Add(TimerParser.PomodoroHandover);
            state = TimerParser.AdvancePomodoro(state, t, "집중", "휴식");
        }

        Assert.Equal(["집중 1/4", "휴식 1/4", "집중 2/4", "휴식 2/4", "집중 3/4", "휴식 3/4", "집중 4/4", "휴식 4/4"], names);
    }

    [Fact]
    public void Pomodoro_last_break_is_long()
    {
        Assert.Equal(15 * 60, TimerParser.ToPomodoro(4, focus: false, Now, "f", "b").Total);
        Assert.Equal(5 * 60, TimerParser.ToPomodoro(2, focus: false, Now, "f", "b").Total);
    }

    [Fact]
    public void Holds_briefly_after_ending_and_paused_does_not_advance()
    {
        var focus = TimerParser.ToPomodoro(1, focus: true, Now, "f", "b");
        Assert.Same(focus, TimerParser.AdvancePomodoro(focus, Now.AddMinutes(25).AddSeconds(1), "f", "b"));
        TimerParser.Pause(focus, Now.AddMinutes(10));
        Assert.Same(focus, TimerParser.AdvancePomodoro(focus, Now.AddHours(2), "f", "b"));
    }

    [Fact]
    public void Runs_with_configured_lengths()
    {
        var lengths = PomodoroLengths.FromMinutes(50, 10, 30);
        var focus = TimerParser.ToPomodoro(1, focus: true, Now, "f", "b", lengths);
        Assert.Equal(50 * 60, focus.Total);
        var rest = TimerParser.AdvancePomodoro(focus, Now.AddMinutes(51), "f", "b", lengths)!;
        Assert.Equal(10 * 60, rest.Total);
    }
}
