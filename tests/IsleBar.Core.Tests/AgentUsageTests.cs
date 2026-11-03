using IsleBar.Core.Island;
using IsleBar.Core.Launch;
using Xunit;

namespace IsleBar.Core.Tests;

public class AgentUsageTests
{
    [Fact]
    public void Claude_status_line_gives_both_windows()
    {
        var w = AgentUsage.FromClaudeStatus("""
            {"model":{"id":"x"},"rate_limits":{"five_hour":{"used_percentage":92.5,"resets_at":1790973732},"seven_day":{"used_percentage":15.3,"resets_at":1791418185}}}
            """);
        Assert.Equal(2, w.Count);
        Assert.Equal("5h", w[0].Key);
        Assert.Equal(92.5, w[0].Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790973732), w[0].ResetsAt);
        Assert.Equal("5h", AgentUsage.ToWarnAbout(w)!.Key);
        Assert.Empty(AgentUsage.FromClaudeStatus("""{"model":{"id":"x"}}"""));   // API key users: no plan windows
        Assert.Empty(AgentUsage.FromClaudeStatus("not json"));
    }

    [Fact]
    public void Codex_record_gives_usage_and_skips_premium_snapshots()
    {
        var w = AgentUsage.FromCodexLine("""
            {"type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"codex","primary":{"used_percent":97.0,"window_minutes":300,"resets_at":1790973732},"secondary":{"used_percent":17.0,"window_minutes":10080,"resets_at":1791418185}}}}
            """)!;
        Assert.Equal(97.0, w.Single(x => x.Key == "5h").Percent);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1790973732), AgentUsage.ResetOfFullest(w, DateTimeOffset.FromUnixTimeSeconds(1790970000)));
        Assert.Null(AgentUsage.FromCodexLine("""{"type":"event_msg","payload":{"type":"token_count","rate_limits":{"limit_id":"premium","primary":null,"secondary":null}}}"""));
        Assert.Null(AgentUsage.FromCodexLine("""{"type":"response_item","payload":{"type":"message"}}"""));
    }

    [Fact]
    public void Codex_failed_turns_are_told_apart()
    {
        var limit = AgentUsage.CodexFailureFrom("""
            {"type":"event_msg","payload":{"type":"task_complete","error":{"message":"You've hit your usage limit.","codex_error_info":"usage_limit_exceeded"}}}
            """)!;
        Assert.True(limit.UsageLimit);
        var auth = AgentUsage.CodexFailureFrom("""{"type":"event_msg","payload":{"type":"task_complete","error":{"message":"x","codex_error_info":"unauthorized"}}}""")!;
        Assert.False(auth.UsageLimit);
        Assert.Equal(StopReason.Login, auth.Reason);
        Assert.Null(AgentUsage.CodexFailureFrom("""{"type":"event_msg","payload":{"type":"task_complete","last_agent_message":"done"}}"""));
    }

    [Fact]
    public void A_usage_window_warns_once()
    {
        var file = Path.Combine(Path.GetTempPath(), "islebar-warned-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var window = new UsageWindow("5h", 91, DateTimeOffset.FromUnixTimeSeconds(1790973732));
            Assert.True(AgentUsage.FirstWarning(file, AgentKind.Codex, window));
            Assert.False(AgentUsage.FirstWarning(file, AgentKind.Codex, window));
            Assert.True(AgentUsage.FirstWarning(file, AgentKind.Claude, window));                                  // another agent
            Assert.True(AgentUsage.FirstWarning(file, AgentKind.Codex, window with { ResetsAt = window.ResetsAt!.Value.AddHours(5) }));   // next window
        }
        finally
        {
            File.Delete(file);
        }
    }

    private static string CodexReading(string timestamp, long primaryReset, long secondaryReset) =>
        "{\"timestamp\":\"" + timestamp + "\",\"type\":\"event_msg\",\"payload\":{\"type\":\"token_count\",\"rate_limits\":{\"limit_id\":\"codex\","
        + "\"primary\":{\"used_percent\":95.0,\"window_minutes\":300,\"resets_at\":" + primaryReset + "},"
        + "\"secondary\":{\"used_percent\":17.0,\"window_minutes\":10080,\"resets_at\":" + secondaryReset + "}}}}";

    [Fact]
    public void Codex_readings_from_before_the_turn_or_past_their_reset_are_ignored()
    {
        var since = DateTimeOffset.Parse("2026-11-03T10:00:00Z");
        var now = since.AddMinutes(2);
        var future = now.AddHours(3).ToUnixTimeSeconds();
        var week = now.AddDays(5).ToUnixTimeSeconds();

        // this turn, windows still running: used
        var fresh = AgentUsage.FreshCodexReading(CodexReading("2026-11-03T10:01:00Z", future, week), since, now);
        Assert.NotNull(fresh);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(future), AgentUsage.ResetOfFullest(fresh, now));

        // codex resume on an old session: the latest reading is from days ago and its 5-hour window already reset
        var resumed = CodexReading("2026-10-30T08:00:00.000Z", now.AddDays(-3).ToUnixTimeSeconds(), week);
        Assert.Null(AgentUsage.FreshCodexReading(resumed, since, now));
        // written before this turn began, even with a reset still ahead
        Assert.Null(AgentUsage.FreshCodexReading(CodexReading("2026-11-03T09:59:59Z", future, week), since, now));
        // this turn, but a window has reset since: stale too
        Assert.Null(AgentUsage.FreshCodexReading(CodexReading("2026-11-03T10:01:00Z", now.AddSeconds(-1).ToUnixTimeSeconds(), week), since, now));
        Assert.Null(AgentUsage.FreshCodexReading("""{"timestamp":"2026-11-03T10:01:00Z","type":"response_item","payload":{"type":"message"}}""", since, now));
    }

    [Fact]
    public void Usage_limit_without_a_usable_reading_lasts_an_hour()
    {
        var now = DateTimeOffset.Parse("2026-11-03T10:00:00Z");
        var past = new[] { new UsageWindow("5h", 100, now.AddMinutes(-10)), new UsageWindow("7d", 40, now.AddDays(3)) };

        // the reading's reset passed during a long turn, or there is none: no end time rather than one already gone
        Assert.Null(AgentUsage.ResetOfFullest(past, now));
        Assert.Null(AgentUsage.ResetOfFullest(null, now));
        Assert.Null(AgentUsage.ResetOfFullest([], now));

        var notice = AgentNotices.UsageLimit(AgentKind.Codex, AgentUsage.ResetOfFullest(past, now), null, now);
        Assert.Equal(now.AddHours(1).ToUnixTimeMilliseconds() / 1000.0, notice.Due);
        Assert.Null(notice.Msg);
    }

    [Fact]
    public void Reset_time_jitter_does_not_warn_again_but_a_new_window_does()
    {
        var file = Path.Combine(Path.GetTempPath(), "islebar-warned-" + Guid.NewGuid().ToString("N") + ".txt");
        try
        {
            var reset = DateTimeOffset.FromUnixTimeSeconds(1790973732);
            var window = new UsageWindow("5h", 91, reset);
            Assert.True(AgentUsage.FirstWarning(file, AgentKind.Codex, window));
            Assert.False(AgentUsage.FirstWarning(file, AgentKind.Codex, window with { Percent = 93, ResetsAt = reset.AddSeconds(1) }));
            Assert.False(AgentUsage.FirstWarning(file, AgentKind.Codex, window with { Percent = 95, ResetsAt = reset.AddSeconds(-1) }));
            Assert.False(AgentUsage.FirstWarning(file, AgentKind.Codex, window with { ResetsAt = reset.AddMinutes(4) }));
            Assert.True(AgentUsage.FirstWarning(file, AgentKind.Codex, window with { Key = "7d" }));                          // other window, same reset
            Assert.True(AgentUsage.FirstWarning(file, AgentKind.Codex, window with { ResetsAt = reset.AddHours(5).AddSeconds(1) }));   // next window
            Assert.True(AgentUsage.FirstWarning(file, AgentKind.Codex, window with { ResetsAt = null }));
            Assert.False(AgentUsage.FirstWarning(file, AgentKind.Codex, window with { ResetsAt = null }));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void Claude_stop_reasons_map_from_error_types()
    {
        Assert.Null(AgentNotices.ClaudeReason("rate_limit"));
        Assert.Equal(StopReason.Login, AgentNotices.ClaudeReason("authentication_failed"));
        Assert.Equal(StopReason.Billing, AgentNotices.ClaudeReason("billing_error"));
        Assert.Equal(StopReason.Busy, AgentNotices.ClaudeReason("overloaded"));
        Assert.Equal(StopReason.Other, AgentNotices.ClaudeReason("max_output_tokens"));
        var stopped = AgentHook.FromClaude("""{"hook_event_name":"StopFailure","session_id":"s","error_type":"authentication_failed"}""")!.Write!;
        Assert.Equal(ActivityRunState.Error, stopped.RunState);
        Assert.Equal("Claude stopped", stopped.Title);
        Assert.Equal("sign in again", stopped.Msg);
    }
}
