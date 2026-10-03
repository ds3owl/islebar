using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public class AgentRecordTests
{
    [Theory]
    [InlineData("""{"parentUuid":"a","isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]},"timestamp":"2026-10-03T08:00:00.000Z"}""")]
    [InlineData("""{"isSidechain":false,"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user for tool use]"}]},"timestamp":"2026-10-03T08:00:00.000Z"}""")]
    [InlineData("""{"type":"user","message":{"role":"user","content":"[Request interrupted by user]"},"timestamp":"2026-10-03T08:00:00.000Z"}""")]
    public void Claude_interrupt_lines_are_found(string line) => Assert.True(AgentRecord.IsClaudeInterrupt(line));

    [Theory]
    // a subagent's own interrupt
    [InlineData("""{"isSidechain":true,"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""")]
    // the marker quoted inside a longer message (a summary, a pasted log)
    [InlineData("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"earlier: 08:37 [Request interrupted by user] then I asked again"}]}}""")]
    // Claude's own text mentioning it
    [InlineData("""{"type":"assistant","message":{"role":"assistant","content":[{"type":"text","text":"[Request interrupted by user]"}]}}""")]
    [InlineData("""not json [Request interrupted by user]""")]
    [InlineData("")]
    public void Other_lines_are_not_interrupts(string line) => Assert.False(AgentRecord.IsClaudeInterrupt(line));

    [Fact]
    public void Prompts_are_told_apart_from_interrupts_and_tool_results()
    {
        Assert.True(AgentRecord.IsClaudePrompt("""{"type":"user","isSidechain":false,"message":{"role":"user","content":"fix the login"}}"""));
        Assert.True(AgentRecord.IsClaudePrompt("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"and add a test"}]}}"""));
        Assert.False(AgentRecord.IsClaudePrompt("""{"type":"user","message":{"role":"user","content":[{"type":"text","text":"[Request interrupted by user]"}]}}"""));
        Assert.False(AgentRecord.IsClaudePrompt("""{"type":"user","message":{"role":"user","content":[{"type":"tool_result","tool_use_id":"x","content":"ok"}]}}"""));
        Assert.False(AgentRecord.IsClaudePrompt("""{"type":"user","isSidechain":true,"message":{"role":"user","content":"sub"}}"""));
        Assert.False(AgentRecord.IsClaudePrompt("""{"type":"assistant","message":{"role":"assistant","content":"hi"}}"""));
        Assert.False(AgentRecord.IsClaudePrompt("""{"type":"user","message":{"role":"user","content":"<command-name>/cost</command-name>"}}"""));
        Assert.False(AgentRecord.IsClaudePrompt("""{"type":"user","message":{"role":"user","content":"<local-command-stdout>Total cost: $0.10</local-command-stdout>"}}"""));
        Assert.False(AgentRecord.IsClaudePrompt("""{"type":"user","isCompactSummary":true,"message":{"role":"user","content":"This session is being continued"}}"""));
    }

    [Fact]
    public void Line_time_reads_the_top_level_timestamp()
    {
        Assert.Equal(new DateTimeOffset(2026, 10, 2, 15, 51, 49, 574, TimeSpan.Zero),
            AgentRecord.LineTime("""{"timestamp":"2026-10-02T15:51:49.574Z","type":"event_msg","payload":{"type":"task_complete"}}"""));
        Assert.Null(AgentRecord.LineTime("""{"type":"event_msg"}"""));
        Assert.Null(AgentRecord.LineTime("""{"timestamp":12}"""));
        Assert.Null(AgentRecord.LineTime("broken {\"timestamp\""));
    }

    [Fact]
    public void Unix_times_in_seconds_or_milliseconds()
    {
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791014422), AgentUsage.FromUnixTime(1791014422));
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1791014422), AgentUsage.FromUnixTime(1791014422000));   // milliseconds used to throw
        Assert.Null(AgentUsage.FromUnixTime(0));
        Assert.Null(AgentUsage.FromUnixTime(-5));
        Assert.Null(AgentUsage.FromUnixTime(long.MaxValue));
    }
}
