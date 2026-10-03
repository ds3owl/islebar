using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class AgentWorkingTests
{
    [Fact]
    public void Prompt_submit_writes_a_working_notice_that_stop_replaces()
    {
        const string submit = """{"hook_event_name":"UserPromptSubmit","session_id":"abc12345","cwd":"C:\\work\\islebar"}""";
        const string stop = """{"hook_event_name":"Stop","session_id":"abc12345","cwd":"C:\\work\\islebar"}""";
        var working = AgentHook.FromClaude(submit)!;
        var done = AgentHook.FromClaude(stop)!;
        Assert.Equal(ActivityKind.AgentWorking, working.Write!.Kind);
        Assert.Equal(working.Id, done.Id);   // same file → the island swaps working for done in place
        Assert.Equal(ActivityKind.AgentDone, done.Write!.Kind);
    }

    [Fact]
    public void Working_stays_while_quiet_but_not_forever()
    {
        var state = new ActivityState { RawKind = ActivityState.KindAgentWorking, State = "run" };
        var now = DateTimeOffset.UtcNow;
        Assert.True(ActivityStore.IsAlive(state, now.AddMinutes(-30), now));
        Assert.False(ActivityStore.IsAlive(state, now.AddHours(-7), now));
    }

    [Fact]
    public void Music_shows_over_a_working_task()
    {
        // A background task working shouldn't hide the song you're playing (user 10-01) — music takes the pill, the task is the dot.
        var music = new ActivityState { RawKind = ActivityState.KindMusic, State = "run" };
        var working = new ActivityState { RawKind = ActivityState.KindAgentWorking, State = "run" };
        Assert.Same(music, IslandSelector.Select([music, working]).Primary);
    }
}
