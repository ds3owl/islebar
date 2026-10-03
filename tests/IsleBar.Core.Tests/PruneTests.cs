using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public class PruneTests
{
    [Fact]
    public void Removes_only_finished_timers_and_expired_notices_keeping_live_ones()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        var now = DateTimeOffset.UtcNow;

        var old = store.Write("old", TimerParser.ToActivity(TimeSpan.FromMinutes(1), now.AddMinutes(-10)));
        File.SetLastWriteTimeUtc(old, now.AddMinutes(-10).UtcDateTime);
        var live = store.Write("live", TimerParser.ToActivity(TimeSpan.FromMinutes(25), now.AddMinutes(-5)));
        File.SetLastWriteTimeUtc(live, now.AddMinutes(-5).UtcDateTime);
        var paused = store.Write("paused", TimerParser.Pause(TimerParser.ToActivity(TimeSpan.FromMinutes(5), now.AddHours(-2)), now.AddHours(-2)));
        File.SetLastWriteTimeUtc(paused, now.AddHours(-2).UtcDateTime);
        // Agent-done now lingers 12 h (so you see it after stepping away), so only one older than that is dead and pruned.
        var done = store.Write("done", new ActivityState { RawKind = ActivityState.KindAgentDone, State = "done" });
        File.SetLastWriteTimeUtc(done, now.AddHours(-13).UtcDateTime);

        Assert.Equal(2, store.PruneDead(now));
        Assert.False(File.Exists(old));
        Assert.False(File.Exists(done));
        Assert.True(File.Exists(live));
        Assert.True(File.Exists(paused));
    }

    [Fact]
    public void Just_finished_items_are_kept_briefly()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        var now = DateTimeOffset.UtcNow;
        var done = store.Write("d", new ActivityState { Title = "📥 Phone → PC", State = "done" });
        File.SetLastWriteTimeUtc(done, now.AddSeconds(-8).UtcDateTime);   // past the 6-second done window but under 10 seconds
        Assert.Equal(0, store.PruneDead(now));
    }

    [Fact]
    public void Missing_folder_is_fine()
        => Assert.Equal(0, new ActivityStore(Path.Combine(Path.GetTempPath(), "islebar-none-" + Guid.NewGuid())).PruneDead(DateTimeOffset.UtcNow));
}
