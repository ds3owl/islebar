using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public class ActivityLifetimeTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 29, 23, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Timer_stays_visible_until_it_ends_even_if_file_is_old()
    {
        // A 25-minute timer started 3 minutes ago — the file hasn't changed for 3 minutes but it must stay alive
        var timer = TimerParser.ToActivity(TimeSpan.FromMinutes(25), Now.AddMinutes(-3));
        Assert.True(ActivityStore.IsAlive(timer, Now.AddMinutes(-3), Now));
    }

    [Theory]
    [InlineData(10, true)]          // away from the desk for 10 minutes: the orange must still be there
    [InlineData(5 * 60, true)]
    [InlineData(7 * 60, false)]   // backstop for a crashed agent
    public void Waiting_for_an_answer_does_not_vanish_after_two_minutes(int ageMinutes, bool alive)
    {
        var ask = new ActivityState { RawKind = ActivityState.KindAgentPermission, Agent = "claude", State = "run" };
        Assert.Equal(alive, ActivityStore.IsAlive(ask, Now.AddMinutes(-ageMinutes), Now));
    }

    [Fact]
    public void Timer_disappears_10_seconds_after_ending()
    {
        var timer = TimerParser.ToActivity(TimeSpan.FromMinutes(1), Now.AddMinutes(-1));
        Assert.True(ActivityStore.IsAlive(timer, Now.AddMinutes(-1), Now.AddSeconds(9)));
        Assert.False(ActivityStore.IsAlive(timer, Now.AddMinutes(-1), Now.AddSeconds(11)));
    }

    [Theory]
    [InlineData("done", 5, true)]
    [InlineData("done", 7, false)]
    [InlineData("error", 11, true)]
    [InlineData("error", 13, false)]
    [InlineData("run", 110, true)]
    [InlineData("run", 130, false)]
    [InlineData("run", -30, true)]   // clock skew stamped it in the future
    public void Transfer_visibility_time_depends_on_state(string state, int ageSeconds, bool alive)
    {
        var transfer = new ActivityState { Title = "📥 Phone → PC", Name = "a.jpg", State = state, Total = 10, Done = 5 };
        Assert.Equal(alive, ActivityStore.IsAlive(transfer, Now.AddSeconds(-ageSeconds), Now));
    }

    [Theory]
    [InlineData(25, true)]
    [InlineData(35, true)]                 // used to vanish at 30 s; now stays so a task finished while you were away is still there
    [InlineData((12 * 3600) + 60, false)]  // gone only after the 12-hour backstop
    public void Agent_done_persists_until_cleared(int ageSeconds, bool alive)
    {
        var done = new ActivityState { RawKind = ActivityState.KindAgentDone, State = "done", Name = "islebar" };
        Assert.Equal(alive, ActivityStore.IsAlive(done, Now.AddSeconds(-ageSeconds), Now));
    }

    [Fact]
    public void Timer_survives_when_read_from_folder()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        var path = store.Write("t", TimerParser.ToActivity(TimeSpan.FromMinutes(25), DateTimeOffset.UtcNow.AddMinutes(-3)));
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-3));

        Assert.Single(store.Read(DateTimeOffset.UtcNow));
    }
}
