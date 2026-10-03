using IsleBar.Core.Configuration;
using IsleBar.Core.Island;
using IsleBar.Core.Launch;
using Xunit;

namespace IsleBar.Core.Tests;

/// <summary>
/// Recent folders, per-agent options, and the island's agent field (owner's requests, 2026-09-29).
/// </summary>
public sealed class RecentFolderTests
{
    // ---------- Recent folders ----------

    [Fact]
    public void New_folder_goes_first()
        => Assert.Equal(["새것", "옛것"], RecentFolders.Add(["옛것"], "새것"));

    [Fact]
    public void Existing_folder_is_moved_to_front()
        => Assert.Equal(["b", "a", "c"], RecentFolders.Add(["a", "b", "c"], "b"));

    [Fact]
    public void Trailing_slash_is_treated_as_same_folder()
    {
        var sep = Path.DirectorySeparatorChar;
        var list = RecentFolders.Add([$"a{sep}b"], $"a{sep}b{sep}");
        Assert.Single(list);
    }

    [Fact]
    public void Case_insensitive()
        => Assert.Single(RecentFolders.Add([@"C:\Work"], @"c:\work"));

    [Fact]
    public void Empty_value_is_not_added()
    {
        Assert.Equal(["a"], RecentFolders.Add(["a"], ""));
        Assert.Equal(["a"], RecentFolders.Add(["a"], "   "));
        Assert.Equal(["a"], RecentFolders.Add(["a"], null));
    }

    [Fact]
    public void Over_the_limit_drops_from_the_end()
    {
        var list = Enumerable.Range(0, RecentFolders.Max).Select(i => "f" + i).ToList();
        var after = RecentFolders.Add(list, "새것");

        Assert.Equal(RecentFolders.Max, after.Count);
        Assert.Equal("새것", after[0]);
        Assert.DoesNotContain("f" + (RecentFolders.Max - 1), after);   // the oldest one is dropped
    }

    [Fact]
    public void Missing_folders_are_filtered_out()
        => Assert.Equal(["있음"], RecentFolders.Prune(["있음", "없음"], f => f == "있음"));

    [Fact]
    public void Normalizing_removes_empty_values_and_duplicates()
        => Assert.Equal(["a", "b"], RecentFolders.Normalize(["a", "", "  ", "A", "b", "a"]));

    [Fact]
    public void Display_name_is_the_last_folder_name()
    {
        var sep = Path.DirectorySeparatorChar;
        Assert.Equal("my_project", RecentFolders.ShortName($"home{sep}workspace{sep}my_project"));
        Assert.Equal("my_project", RecentFolders.ShortName($"home{sep}workspace{sep}my_project{sep}"));
        Assert.Equal("", RecentFolders.ShortName("  "));
    }

    // ---------- Per-agent options ----------

    [Fact]
    public void Each_agent_has_its_own_options()
    {
        var s = new IsleBarSettings();
        s.Values.Model = "opus";
        s.CodexValues.Model = "gpt-6-astra";

        Assert.Equal("opus", s.ValuesFor(AgentKind.Claude).Model);
        Assert.Equal("gpt-6-astra", s.ValuesFor(AgentKind.Codex).Model);
    }

    [Fact]
    public void Clone_does_not_mix_the_two_sets()
    {
        var s = new IsleBarSettings();
        s.CodexValues.Perm = "auto";
        s.RecentFolders.Add("a");

        var copy = s.Clone();
        copy.CodexValues.Perm = "ask";
        copy.RecentFolders.Add("b");

        Assert.Equal("auto", s.CodexValues.Perm);
        Assert.Equal(["a"], s.RecentFolders);
    }

    [Fact]
    public void Both_sets_and_recent_folders_survive_settings_round_trip()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");

        var s = new IsleBarSettings { Agent = AgentKind.Codex };
        s.Values.Model = "opus";
        s.CodexValues.Model = "gpt-5.5";
        s.CodexValues.Perm = "auto";
        s.RecentFolders = ["첫폴더", "둘째폴더"];
        new ConfigStore(path).Save(s);

        var back = new ConfigStore(path).Load();
        Assert.Equal(AgentKind.Codex, back.Agent);
        Assert.Equal("opus", back.Values.Model);
        Assert.Equal("gpt-5.5", back.CodexValues.Model);
        Assert.Equal("auto", back.CodexValues.Perm);
        Assert.Equal(["첫폴더", "둘째폴더"], back.RecentFolders);
    }

    [Fact]
    public void Codex_model_is_validated_against_codex_list()
    {
        using var dir = new TempDir();
        var path = dir.File("islebar.json");
        File.WriteAllText(path, """
            {"agent":"codex",
             "values":{"model":"opus"},
             "codex_values":{"model":"gpt-6-astra"}}
            """);

        var s = new ConfigStore(path).Load();
        Assert.Equal("opus", s.Values.Model);            // Claude side against the Claude list
        Assert.Equal("gpt-6-astra", s.CodexValues.Model); // Codex side against the Codex list
    }

    // ---------- Island agent field ----------

    [Fact]
    public void Island_shows_which_agent()
    {
        Assert.Equal("Codex", new ActivityState { Agent = "codex" }.AgentLabel);
        Assert.Equal("Codex", new ActivityState { Agent = "CODEX" }.AgentLabel);
        Assert.Equal("Claude", new ActivityState { Agent = "claude" }.AgentLabel);
        Assert.Equal("Claude", new ActivityState().AgentLabel);   // no field → Claude
    }

    [Theory]
    [InlineData("agent-done", ActivityKind.AgentDone)]
    [InlineData("agent-permission", ActivityKind.AgentPermission)]
    [InlineData("claude-done", ActivityKind.AgentDone)]              // hooks already configured
    [InlineData("claude-permission", ActivityKind.AgentPermission)]  // hooks already configured
    public void Legacy_names_are_still_read(string raw, ActivityKind expected)
        => Assert.Equal(expected, ActivityState.ParseKind(raw));

    [Fact]
    public void Agent_field_is_written_to_the_file_too()
    {
        using var dir = new TempDir();
        var store = new ActivityStore(dir.Path);
        store.Write("코덱스작업", new ActivityState
        {
            RawKind = ActivityState.KindAgentDone,
            Agent = AgentKind.Codex,
            Title = "Codex",
            Name = "고치기",
            State = "done",
        });

        var back = store.Read(DateTimeOffset.UtcNow).Single();
        Assert.Equal(AgentKind.Codex, back.Agent);
        Assert.Equal("Codex", back.AgentLabel);
        Assert.Equal(ActivityKind.AgentDone, back.Kind);
    }
}
