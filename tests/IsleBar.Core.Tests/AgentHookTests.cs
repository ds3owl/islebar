using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public class AgentHookTests
{
    [Theory]
    [InlineData("UserPromptSubmit", "agent-working")]
    [InlineData("PermissionRequest", "agent-permission")]
    [InlineData("Stop", "agent-done")]
    public void Codex_lifecycle_hooks_light_the_pill_like_Claude(string hookEvent, string kind)
    {
        var action = AgentHook.FromCodexHook($$"""{"hook_event_name":"{{hookEvent}}","session_id":"019a2b3c-4d5e","cwd":"C:\\Users\\me\\ClaudeBar","turn_id":"t1"}""")!;

        Assert.Equal("codex_019a2b3c", action.Id);
        Assert.Equal(kind, action.Write!.RawKind);
        Assert.Equal("codex", action.Write.Agent);
        Assert.Null(action.OnlyOver);
    }

    [Fact]
    public void Codex_prompt_becomes_the_name_and_later_events_fall_back_to_the_folder()
    {
        var start = AgentHook.FromCodexHook("""{"hook_event_name":"UserPromptSubmit","session_id":"s","cwd":"C:\\p\\isle","prompt":"  Fix   the login bug\nand add tests"}""")!;
        Assert.Equal("Fix the login bug", start.Write!.Name);
        var done = AgentHook.FromCodexHook("""{"hook_event_name":"Stop","session_id":"s","cwd":"C:\\p\\isle"}""")!;
        Assert.Null(done.Write!.Name);   // the CLI keeps the prompt name already on the pill
        Assert.Equal("isle", done.FallbackName);
        Assert.Equal(40, AgentHook.PromptName(new string('가', 60))!.Length);
        Assert.Null(AgentHook.PromptName("   "));
    }

    [Theory]
    [InlineData("permission_prompt", true)]
    [InlineData("elicitation_dialog", true)]
    [InlineData("auth_success", false)]   // used to become a 6-hour orange (code review 10-01)
    [InlineData("idle_prompt", false)]
    public void Only_notifications_that_need_an_answer_turn_orange(string type, bool orange)
    {
        var action = AgentHook.FromClaude($$"""{"hook_event_name":"Notification","session_id":"s","notification_type":"{{type}}","message":"x"}""");
        Assert.Equal(orange, action?.Write?.Kind == ActivityKind.AgentPermission);
    }

    [Fact]
    public void Prompt_name_never_splits_an_emoji()
    {
        var name = AgentHook.PromptName(new string('a', 38) + "😀😀 tail")!;
        Assert.DoesNotContain('�', name);
        Assert.False(char.IsHighSurrogate(name[^2]));   // the char before "…" is never half of a pair
    }

    [Fact]
    public void Codex_post_tool_use_only_turns_orange_back_to_working()
    {
        var action = AgentHook.FromCodexHook("""{"hook_event_name":"PostToolUse","session_id":"019a2b3c"}""")!;
        Assert.Equal("agent-working", action.Write!.RawKind);
        Assert.Equal(ActivityKind.AgentPermission, action.OnlyOver);
    }

    [Fact]
    public void Codex_session_end_clears_and_unknown_events_do_nothing()
    {
        Assert.Null(AgentHook.FromCodexHook("""{"hook_event_name":"SessionEnd","session_id":"019a2b3c"}""")!.Write);
        Assert.Null(AgentHook.FromCodexHook("""{"hook_event_name":"PreCompact","session_id":"019a2b3c"}"""));
        Assert.Null(AgentHook.FromCodexHook("not json"));
        Assert.Null(AgentHook.FromCodexHook(""));
    }

    [Fact]
    public void Claude_permission_notification_rings_the_bell()
    {
        var action = AgentHook.FromClaude("""
            {"hook_event_name":"Notification","session_id":"8553593a-559c","cwd":"C:\\Users\\me\\islebar",
             "message":"Claude needs your permission to use Bash"}
            """)!;

        Assert.Equal("claude_8553593a", action.Id);
        Assert.Equal(ActivityKind.AgentPermission, action.Write!.Kind);
        Assert.Equal("islebar", action.Write.Name);
        Assert.Null(action.Write.Msg);   // the long English sentence is not carried (the island shows only "Claude · folder")
        Assert.Equal("Claude", action.Write.AgentLabel);
    }

    [Fact]
    public void Claude_stop_overwrites_with_done_under_same_id()
    {
        var ask = AgentHook.FromClaude("""{"hook_event_name":"Notification","session_id":"abc","cwd":"/p/x"}""")!;
        var done = AgentHook.FromClaude("""{"hook_event_name":"Stop","session_id":"abc","cwd":"/p/x"}""")!;

        Assert.Equal(ask.Id, done.Id);
        Assert.Equal(ActivityKind.AgentDone, done.Write!.Kind);
        Assert.Equal(ActivityRunState.Done, done.Write.RunState);
        Assert.Null(done.Write.Name);   // done doesn't show the folder name (only when there's a session name)
    }

    [Theory]
    [InlineData("""{"hook_event_name":"Notification","session_id":"a","message":"Claude is waiting for your input"}""")]
    [InlineData("""{"hook_event_name":"Notification","session_id":"a","notification_type":"idle_prompt","message":"x"}""")]
    public void Input_waiting_notification_is_ignored(string json)
        => Assert.Null(AgentHook.FromClaude(json));

    [Fact]
    public void Newer_permission_notification_is_recognised_by_type()
    {
        var action = AgentHook.FromClaude("""{"hook_event_name":"Notification","session_id":"a","notification_type":"permission_prompt","message":"waiting for your input"}""");
        Assert.Equal(ActivityKind.AgentPermission, action!.Write!.Kind);
    }

    [Fact]
    public void Claude_session_end_removes_it()
    {
        var action = AgentHook.FromClaude("""{"hook_event_name":"SessionEnd","session_id":"abc"}""")!;
        Assert.Null(action.Write);
        Assert.Equal("claude_abc", action.Id);
    }

    [Theory]
    [InlineData("""{"hook_event_name":"PreToolUse","session_id":"a"}""")]
    [InlineData("깨짐")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("[1]")]
    public void Unknown_input_does_nothing(string? json)
        => Assert.Null(AgentHook.FromClaude(json));

    [Fact]
    public void Codex_turn_complete_is_done()
    {
        var action = AgentHook.FromCodex("""
            {"type":"agent-turn-complete","thread-id":"019a-77ff","turn-id":"12","cwd":"/home/me/proj",
             "last-assistant-message":"done"}
            """)!;

        Assert.Equal("codex_019a77ff", action.Id);
        Assert.Equal(ActivityKind.AgentDone, action.Write!.Kind);
        Assert.Equal("Codex", action.Write.AgentLabel);
        Assert.Equal("proj", action.Write.Name);
    }

    [Fact]
    public void Codex_other_notifications_are_ignored()
        => Assert.Null(AgentHook.FromCodex("""{"type":"something-else"}"""));

    [Theory]
    [InlineData(@"C:\Users\me\islebar\", "islebar")]
    [InlineData("/home/me/proj", "proj")]
    [InlineData(@"C:\", "C:")]
    [InlineData("", null)]
    public void Folder_name(string cwd, string? expected)
        => Assert.Equal(expected, AgentHook.FolderName(cwd));

    [Fact]
    public void Id_is_safe_for_file_names()
    {
        Assert.Equal("claude_abc12345", AgentHook.IdFor("claude", "a/b\\c:1*2?3\"4<5>6|78"));
        Assert.Equal("codex_agent", AgentHook.IdFor("codex", null));
    }

    [Fact]
    public void Uses_launch_project_folder_name_when_given()
    {
        var json = """{"hook_event_name":"Notification","session_id":"a","cwd":"C:\\Users\\me\\.claude\\memory"}""";
        Assert.Equal("memory", AgentHook.FromClaude(json)!.Write!.Name);
        Assert.Equal("me", AgentHook.FromClaude(json, @"C:\Users\me")!.Write!.Name);
        Assert.Equal("memory", AgentHook.FromClaude(json, "  ")!.Write!.Name);
    }

    [Fact]
    public void Session_name_is_used_for_both_done_and_permission()
    {
        string? Title(string? path) => path == "t.jsonl" ? "한글 타이핑 위치 문제" : null;
        var done = AgentHook.FromClaude("""{"hook_event_name":"Stop","session_id":"a","cwd":"/p/x","transcript_path":"t.jsonl"}""", null, Title)!;
        var ask = AgentHook.FromClaude("""{"hook_event_name":"Notification","session_id":"a","cwd":"/p/x","transcript_path":"t.jsonl"}""", null, Title)!;
        Assert.Equal("한글 타이핑 위치 문제", done.Write!.Name);
        Assert.Equal("한글 타이핑 위치 문제", ask.Write!.Name);
    }

    [Fact]
    public void Tool_calls_do_not_read_the_transcript_for_the_title()
    {
        var action = AgentHook.FromClaude("""{"hook_event_name":"PostToolUse","session_id":"abc","transcript_path":"t"}""",
            titleOf: _ => throw new InvalidOperationException("transcript read on a tool call"))!;
        Assert.Equal(ActivityKind.AgentPermission, action.OnlyOver);
        Assert.Null(action.Write!.Name);
    }

    [Fact]
    public void A_usage_limit_is_a_red_notice_that_lasts_until_the_reset()
    {
        var reset = DateTimeOffset.UtcNow.AddHours(2).ToUnixTimeSeconds();
        var action = AgentHook.FromClaude("{\"hook_event_name\":\"StopFailure\",\"session_id\":\"abc\",\"error_type\":\"rate_limit\",\"error\":\"x\",\"rate_limit\":{\"reset_time\":" + reset + "}}",
            text: IsleBar.Core.Localization.LanguageCatalog.For("ko"))!;
        var state = action.Write!;
        Assert.Equal(ActivityKind.Notice, state.Kind);
        Assert.Equal(ActivityRunState.Error, state.RunState);
        Assert.Equal("Claude 사용 한도 도달", state.Title);
        Assert.EndsWith("초기화", state.Msg);
        Assert.Equal(reset, (long)state.Due!.Value);
        Assert.True(ActivityStore.IsAlive(state, DateTimeOffset.UtcNow.AddMinutes(90), DateTimeOffset.UtcNow.AddMinutes(90)));

        Assert.Equal("서버 혼잡 · 잠시 후 다시", AgentHook.FromClaude("""{"hook_event_name":"StopFailure","session_id":"abc","error_type":"server_error"}""",
            text: IsleBar.Core.Localization.LanguageCatalog.For("ko"))!.Write!.Msg);
    }
}
