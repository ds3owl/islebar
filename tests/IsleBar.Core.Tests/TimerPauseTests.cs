using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public class TimerPauseTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 29, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Pausing_freezes_remaining_time()
    {
        var timer = TimerParser.ToActivity(TimeSpan.FromMinutes(10), Start);
        TimerParser.Pause(timer, Start.AddMinutes(3));

        Assert.True(timer.IsPaused);
        Assert.Null(timer.Due);
        Assert.Equal(TimeSpan.FromMinutes(7), TimerParser.Remaining(timer, Start.AddMinutes(3)));
        Assert.Equal(TimeSpan.FromMinutes(7), TimerParser.Remaining(timer, Start.AddHours(2)));   // doesn't decrease while paused
        Assert.Equal(0.3, TimerParser.Fraction(timer, Start.AddHours(2))!.Value, 3);
    }

    [Fact]
    public void Resuming_continues_from_where_it_paused()
    {
        var timer = TimerParser.ToActivity(TimeSpan.FromMinutes(10), Start);
        TimerParser.Pause(timer, Start.AddMinutes(3));
        TimerParser.Resume(timer, Start.AddMinutes(30));

        Assert.False(timer.IsPaused);
        Assert.Equal(TimeSpan.FromMinutes(6), TimerParser.Remaining(timer, Start.AddMinutes(31)));
        Assert.Equal(0.4, TimerParser.Fraction(timer, Start.AddMinutes(31))!.Value, 3);
    }

    [Fact]
    public void Paused_timer_stays_alive_for_long()
    {
        var timer = TimerParser.Pause(TimerParser.ToActivity(TimeSpan.FromMinutes(1), Start), Start.AddSeconds(10));
        Assert.True(ActivityStore.IsAlive(timer, Start.AddSeconds(10), Start.AddHours(3)));
    }

    [Fact]
    public void Double_pause_or_resuming_unpaused_changes_nothing()
    {
        var timer = TimerParser.ToActivity(TimeSpan.FromMinutes(10), Start);
        TimerParser.Resume(timer, Start.AddMinutes(1));
        Assert.Equal(TimeSpan.FromMinutes(9), TimerParser.Remaining(timer, Start.AddMinutes(1)));

        TimerParser.Pause(timer, Start.AddMinutes(2));
        TimerParser.Pause(timer, Start.AddMinutes(5));
        Assert.Equal(TimeSpan.FromMinutes(8), TimerParser.Remaining(timer, Start.AddMinutes(5)));
    }

    [Fact]
    public void Paused_state_is_saved_to_file_and_read_back()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        var path = store.Write("t", TimerParser.Pause(TimerParser.ToActivity(TimeSpan.FromMinutes(5), DateTimeOffset.UtcNow), DateTimeOffset.UtcNow));
        var back = ActivityStore.ReadFile(path)!;
        Assert.True(back.IsPaused);
        Assert.Single(store.Read(DateTimeOffset.UtcNow));
    }
}
