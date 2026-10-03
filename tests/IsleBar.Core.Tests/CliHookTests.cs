using IsleBar.Cli;
using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public class CliHookTests
{
    private static (int Code, string Out) Hook(string stdin, params string[] args)
    {
        var output = new StringWriter();
        var code = CommandRunner.Run(["hook", .. args], output, new StringWriter(), new StringReader(stdin));
        return (code, output.ToString());
    }

    [Fact]
    public void Claude_hook_writes_permission_and_stop_overwrites_it()
    {
        using var dir = new TempDir();
        var ask = Hook("""{"hook_event_name":"Notification","session_id":"s1","cwd":"C:/p/isle","message":"need permission"}""", "--dir", dir.Path);
        Assert.Equal((0, ""), ask);   // no output (Claude reads hook output)
        var live = new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow);
        Assert.Equal(ActivityKind.AgentPermission, Assert.Single(live).Kind);

        Hook("""{"hook_event_name":"Stop","session_id":"s1","cwd":"C:/p/isle"}""", "--dir", dir.Path);
        live = new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow);
        Assert.Equal(ActivityKind.AgentDone, Assert.Single(live).Kind);

        Hook("""{"hook_event_name":"SessionEnd","session_id":"s1"}""", "--dir", dir.Path);
        Assert.Empty(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void Codex_lifecycle_hook_on_stdin_lights_working_then_done()
    {
        using var dir = new TempDir();
        Hook("""{"session_id":"01a0f5f5-98ca","cwd":"C:\\Users\\me\\ClaudeBar","hook_event_name":"UserPromptSubmit","prompt":"Fix the login bug"}""", "--agent", "codex", "--dir", dir.Path);
        var working = Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));
        Assert.Equal(ActivityKind.AgentWorking, working.Kind);
        Assert.Equal("codex", working.Agent);
        Assert.Equal("Fix the login bug", working.Name);

        Hook("""{"session_id":"01a0f5f5-98ca","cwd":"C:\\Users\\me\\ClaudeBar","hook_event_name":"Stop"}""", "--agent", "codex", "--dir", dir.Path);
        var done = Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));
        Assert.Equal(ActivityKind.AgentDone, done.Kind);
        Assert.Equal("Fix the login bug", done.Name);   // the prompt name carries over to "done"
    }

    [Fact]
    public void Claude_done_keeps_its_own_naming_after_a_permission_prompt()
    {
        // Claude's "done" shows no folder when the session has no title — the Codex name carry-over must not change that (code review 10-01)
        using var dir = new TempDir();
        Hook("""{"hook_event_name":"Notification","session_id":"s1","cwd":"C:/p/isle","notification_type":"permission_prompt"}""", "--dir", dir.Path);
        Hook("""{"hook_event_name":"Stop","session_id":"s1","cwd":"C:/p/isle"}""", "--dir", dir.Path);
        var done = Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));
        Assert.Equal(ActivityKind.AgentDone, done.Kind);
        Assert.Null(done.Name);
    }

    [Fact]
    public void Codex_done_without_an_earlier_state_falls_back_to_the_folder()
    {
        using var dir = new TempDir();
        Hook("""{"session_id":"01a0f5f5-98ca","cwd":"C:\\Users\\me\\ClaudeBar","hook_event_name":"Stop"}""", "--agent", "codex", "--dir", dir.Path);
        Assert.Equal("ClaudeBar", Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow)).Name);
    }

    [Fact]
    public void Answering_a_permission_prompt_turns_it_back_into_working()
    {
        using var dir = new TempDir();
        Hook("""{"hook_event_name":"Notification","session_id":"s1","cwd":"C:/p/isle","message":"need permission"}""", "--dir", dir.Path);
        Hook("""{"hook_event_name":"PostToolUse","session_id":"s1","cwd":"C:/p/isle","tool_name":"AskUserQuestion"}""", "--dir", dir.Path);
        var state = Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));
        Assert.Equal(ActivityKind.AgentWorking, state.Kind);
        Assert.Equal("isle", state.Name);   // the permission state's name is kept (not re-read from the transcript)
    }

    [Fact]
    public void An_interrupted_tool_clears_a_running_turn_but_not_a_done_one()
    {
        using var dir = new TempDir();
        Hook("""{"hook_event_name":"Notification","session_id":"s1","cwd":"C:/p/isle","message":"need permission"}""", "--dir", dir.Path);
        Hook("""{"hook_event_name":"PostToolUseFailure","session_id":"s1","tool_name":"Bash","is_interrupt":true}""", "--dir", dir.Path);
        Assert.Empty(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));

        Hook("""{"hook_event_name":"UserPromptSubmit","session_id":"s1","cwd":"C:/p/isle"}""", "--dir", dir.Path);
        Hook("""{"hook_event_name":"PostToolUseFailure","session_id":"s1","tool_name":"Bash","is_interrupt":false}""", "--dir", dir.Path);
        Assert.Equal(ActivityKind.AgentWorking, Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow)).Kind);
        Hook("""{"hook_event_name":"PostToolUseFailure","session_id":"s1","tool_name":"Bash","is_interrupt":true}""", "--dir", dir.Path);
        Assert.Empty(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));

        Hook("""{"hook_event_name":"Stop","session_id":"s1"}""", "--dir", dir.Path);
        Hook("""{"hook_event_name":"PostToolUseFailure","session_id":"s1","tool_name":"Bash","is_interrupt":true}""", "--dir", dir.Path);
        Assert.Equal(ActivityKind.AgentDone, Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow)).Kind);
    }

    [Fact]
    public void Ordinary_tool_calls_leave_other_states_alone()
    {
        using var dir = new TempDir();
        Hook("""{"hook_event_name":"PostToolUse","session_id":"s1","tool_name":"Bash"}""", "--dir", dir.Path);
        Assert.Empty(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));   // nothing was pending → nothing written

        Hook("""{"hook_event_name":"Stop","session_id":"s1"}""", "--dir", dir.Path);
        Hook("""{"hook_event_name":"PostToolUseFailure","session_id":"s1","tool_name":"Bash"}""", "--dir", dir.Path);
        Assert.Equal(ActivityKind.AgentDone, Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow)).Kind);
    }

    [Fact]
    public void Codex_notify_is_taken_from_last_argument()
    {
        using var dir = new TempDir();
        Hook("", "--agent", "codex", "--dir", dir.Path, """{"type":"agent-turn-complete","thread-id":"t9","cwd":"/w/proj"}""");
        var state = Assert.Single(new ActivityStore(dir.Path).Read(DateTimeOffset.UtcNow));
        Assert.Equal("Codex", state.AgentLabel);
        Assert.Equal("proj", state.Name);
    }

    [Theory]
    [InlineData("broken input")]
    [InlineData("")]
    public void Bad_input_quietly_returns_0(string stdin)
    {
        using var dir = new TempDir();
        Assert.Equal((0, ""), Hook(stdin, "--dir", dir.Path));
        Assert.Equal((0, ""), Hook(stdin, "--dir", dir.Path, "--unknown-option"));
    }
}
