using IsleBar.Cli;
using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class CliTests
{
    private static (int Code, string Out, string Err) Run(params string[] args)
    {
        var output = new StringWriter();
        var error = new StringWriter();
        var code = CommandRunner.Run(args, output, error);
        return (code, output.ToString(), error.ToString());
    }

    [Fact]
    public void No_arguments_prints_help_and_usage_error()
    {
        var (code, output, _) = Run();
        Assert.Equal(CommandRunner.ExitUsage, code);
        Assert.Contains("islebar push", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Help_succeeds()
    {
        var (code, output, _) = Run("--help");
        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Contains("islebar timer", output, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_command()
    {
        var (code, _, error) = Run("fly");
        Assert.Equal(CommandRunner.ExitUsage, code);
        Assert.Contains("unknown command", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Push_creates_a_readable_state_file()
    {
        using var dir = new TempDir();
        var (code, output, error) = Run(
            "push", "--dir", dir.Path, "--id", "sending", "--kind", "transfer",
            "--title", "📥 Phone → PC", "--name", "clip 01.mp4",
            "--total", "81920000", "--done", "4096000", "--state", "run");

        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Equal("", error);

        var path = output.Trim();
        Assert.True(File.Exists(path));
        var state = ActivityStore.ReadFile(path)!;
        Assert.Equal(ActivityKind.Transfer, state.Kind);
        Assert.Equal("📥 Phone → PC", state.Title);
        Assert.Equal("clip 01.mp4", state.Name);
        Assert.Equal(0.05, state.Fraction!.Value, 3);
        Assert.NotNull(state.T0);              // filled with the current time if not given
    }

    [Fact]
    public void Without_done_the_fraction_is_unknown()
    {
        using var dir = new TempDir();
        var (_, output, _) = Run("push", "--dir", dir.Path, "--id", "a", "--total", "100");
        var state = ActivityStore.ReadFile(output.Trim())!;
        Assert.True(state.IsIndeterminate);
        Assert.Null(state.Fraction);
    }

    [Fact]
    public void Invalid_kind_is_usage_error()
    {
        using var dir = new TempDir();
        var (code, _, error) = Run("push", "--dir", dir.Path, "--kind", "nosuchkind");
        Assert.Equal(CommandRunner.ExitUsage, code);
        Assert.Contains("--kind", error, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(dir.Path));   // don't write a bogus state
    }

    [Fact]
    public void Invalid_state_is_usage_error()
    {
        using var dir = new TempDir();
        var (code, _, error) = Run("push", "--dir", dir.Path, "--state", "dunno");
        Assert.Equal(CommandRunner.ExitUsage, code);
        Assert.Contains("--state", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Unknown_option_is_reported_and_continues()
    {
        using var dir = new TempDir();
        var (code, _, error) = Run("push", "--dir", dir.Path, "--id", "a", "--typo", "value");
        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Contains("--typo", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Show_picks_by_priority()
    {
        using var dir = new TempDir();
        Run("push", "--dir", dir.Path, "--id", "music", "--kind", "music", "--name", "song");
        Run("push", "--dir", dir.Path, "--id", "transfer", "--kind", "transfer", "--name", "file");
        Run("push", "--dir", dir.Path, "--id", "perm", "--kind", "claude-permission", "--name", "Delete it?");

        var (code, output, _) = Run("show", "--dir", dir.Path);
        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Contains("AgentPermission", output, StringComparison.Ordinal);   // the old name claude-permission is still read
        Assert.Contains("Delete it?", output, StringComparison.Ordinal);
        Assert.Contains("+2", output, StringComparison.Ordinal);
    }

    [Fact]
    public void With_nothing_says_the_island_is_empty()
    {
        using var dir = new TempDir();
        var (code, output, _) = Run("show", "--dir", dir.Path);
        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Contains("empty", output, StringComparison.Ordinal);
    }

    [Fact]
    public void List_shows_everything()
    {
        using var dir = new TempDir();
        Run("push", "--dir", dir.Path, "--id", "a", "--name", "first");
        Run("push", "--dir", dir.Path, "--id", "b", "--name", "second");

        var (code, output, _) = Run("list", "--dir", dir.Path);
        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Contains("first", output, StringComparison.Ordinal);
        Assert.Contains("second", output, StringComparison.Ordinal);
    }

    [Fact]
    public void rm()
    {
        using var dir = new TempDir();
        Run("push", "--dir", dir.Path, "--id", "to-delete");

        Assert.Equal(CommandRunner.ExitOk, Run("rm", "to-delete", "--dir", dir.Path).Code);
        Assert.Equal(CommandRunner.ExitFailed, Run("rm", "to-delete", "--dir", dir.Path).Code);
    }

    [Fact]
    public void Rm_takes_exactly_one_id()
    {
        using var dir = new TempDir();
        Assert.Equal(CommandRunner.ExitUsage, Run("rm", "--dir", dir.Path).Code);
        Assert.Equal(CommandRunner.ExitUsage, Run("rm", "a", "b", "--dir", dir.Path).Code);
    }

    /// <summary>
    /// If option values leak into the remaining words, `timer --dir X 25분` tries to read "X 25분" as a duration.
    /// (A bug we actually hit — kept so it doesn't break again after the fix.)
    /// </summary>
    [Fact]
    public void Timer_does_not_read_option_values_as_duration()
    {
        using var dir = new TempDir();
        var (code, output, error) = Run("timer", "--dir", dir.Path, "1시간 30분");

        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Equal("", error);
        Assert.Contains("5400", output, StringComparison.Ordinal);

        var state = ActivityStore.ReadFile(Directory.GetFiles(dir.Path, "*.json").Single())!;
        Assert.Equal(ActivityKind.Timer, state.Kind);
        Assert.Equal(5400, state.Total);
    }

    [Fact]
    public void Timer_allows_options_after_duration()
    {
        using var dir = new TempDir();
        var (code, output, _) = Run("timer", "25분", "--dir", dir.Path, "--label", "Pomodoro");
        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Contains("1500", output, StringComparison.Ordinal);

        var state = ActivityStore.ReadFile(Directory.GetFiles(dir.Path, "*.json").Single())!;
        Assert.Equal("Pomodoro", state.Name);
    }

    [Fact]
    public void Non_duration_is_usage_error()
    {
        using var dir = new TempDir();
        var (code, _, error) = Run("timer", "--dir", dir.Path, "25");
        Assert.Equal(CommandRunner.ExitUsage, code);
        Assert.Contains("25m", error, StringComparison.Ordinal);   // shows an example
        Assert.Empty(Directory.GetFiles(dir.Path));
    }

    [Fact]
    public void Args_previews_launch_arguments()
    {
        var (code, output, _) = Run("args", "why so slow?", "--model", "opus", "--rc");
        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Equal("""claude "why so slow?" --dangerously-skip-permissions --model opus[1m] --remote-control""", output.Trim());
    }

    [Fact]
    public void Args_also_does_not_read_option_values_as_question()
    {
        var (_, output, _) = Run("args", "--session", "resume", "hello");
        Assert.Equal("claude hello --dangerously-skip-permissions --resume", output.Trim());
    }

    [Fact]
    public void Aliases_reads_from_fixed_copy()
    {
        var fixture = Path.Combine(AppContext.BaseDirectory, "Fixtures", "model-aliases-sample.md");
        var (code, output, _) = Run("aliases", fixture);
        Assert.Equal(CommandRunner.ExitOk, code);
        Assert.Equal("fable sonnet opus haiku", output.Trim());
    }

    [Fact]
    public void Untrustworthy_document_reports_failure()
    {
        using var dir = new TempDir();
        var path = dir.File("bad-doc.md");
        File.WriteAllText(path, "### Model aliases\n**`opus`**");

        var (code, _, error) = Run("aliases", path);
        Assert.Equal(CommandRunner.ExitFailed, code);
        Assert.Contains("unreliable", error, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_folder_ends_with_error()
    {
        var (code, _, error) = Run("aliases", "/no/such/file.md");
        Assert.Equal(CommandRunner.ExitFailed, code);
        Assert.NotEqual("", error);
    }
}
