using IsleBar.Core.Configuration;
using IsleBar.Core.Launch;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class ClaudeArgumentsTests
{
    private const string Exe = @"C:\Users\me\.local\bin\claude.exe";

    private static LaunchOptions Opts(
        string session = "new", string model = "default", string effort = "default",
        string perm = "bypass", bool rc = false)
        => new() { Session = session, Model = model, Effort = effort, Perm = perm, Rc = rc };

    [Fact]
    public void Defaults_are_only_bypass_after_question()
    {
        Assert.Equal(
            [Exe, "build a taskbar search box", "--dangerously-skip-permissions"],
            ClaudeArguments.Build(Exe, "build a taskbar search box", Opts()));
    }

    [Fact]
    public void Question_comes_first()
    {
        // --remote-control and --resume take values, so a question after them would be eaten as the session name
        var args = ClaudeArguments.Build(Exe, "why so slow?", Opts(session: "resume", rc: true));

        Assert.Equal(Exe, args[0]);
        Assert.Equal("why so slow?", args[1]);
        Assert.True(args.ToList().IndexOf("--resume") > 1);
        Assert.True(args.ToList().IndexOf("--remote-control") > 1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Empty_question_runs_without_question(string? question)
    {
        Assert.Equal(
            [Exe, "--dangerously-skip-permissions"],
            ClaudeArguments.Build(Exe, question, Opts()));
    }

    [Theory]
    [InlineData("-v why so slow?", " -v why so slow?")]
    [InlineData("--help what is this", " --help what is this")]
    [InlineData("-", " -")]
    [InlineData("  -p this  ", " -p this")]     // trim first, then prepend the space
    public void Question_starting_with_hyphen_gets_leading_space(string question, string expected)
    {
        var args = ClaudeArguments.Build(Exe, question, Opts());
        Assert.Equal(expected, args[1]);
    }

    [Fact]
    public void Hyphen_in_the_middle_is_left_alone()
        => Assert.Equal("this -v why?", ClaudeArguments.Build(Exe, "this -v why?", Opts())[1]);

    // ---------------- perm ----------------

    [Theory]
    [InlineData("bypass", new[] { "--dangerously-skip-permissions" })]
    [InlineData("auto", new[] { "--permission-mode", "auto" })]
    [InlineData("plan", new[] { "--permission-mode", "plan" })]
    [InlineData("ask", new string[0])]                              // claude's default behaviour
    public void Permission(string perm, string[] expected)
        => Assert.Equal([Exe, .. expected], ClaudeArguments.Build(Exe, "", Opts(perm: perm)));

    // ---------------- session ----------------

    [Theory]
    [InlineData("new", new string[0])]
    [InlineData("continue", new[] { "--continue" })]
    [InlineData("resume", new[] { "--resume" })]
    public void Session(string session, string[] expected)
        => Assert.Equal(
            [Exe, "--dangerously-skip-permissions", .. expected],
            ClaudeArguments.Build(Exe, "", Opts(session: session)));

    // ---------------- model ----------------

    [Fact]
    public void Opus_is_sent_as_long_context_variant()
        => Assert.Equal(
            [Exe, "--dangerously-skip-permissions", "--model", "opus[1m]"],
            ClaudeArguments.Build(Exe, "", Opts(model: "opus")));

    [Theory]
    [InlineData("sonnet")]
    [InlineData("haiku")]
    [InlineData("fable")]
    [InlineData("lyric")]          // new families that don't exist yet are passed through too
    public void Other_models_pass_alias_as_is(string model)
        => Assert.Equal(
            [Exe, "--dangerously-skip-permissions", "--model", model],
            ClaudeArguments.Build(Exe, "", Opts(model: model)));

    [Fact]
    public void Default_model_adds_no_argument()
        => Assert.DoesNotContain("--model", ClaudeArguments.Build(Exe, "", Opts(model: "default")));

    // ---------------- effort ----------------

    [Theory]
    [InlineData("low")]
    [InlineData("medium")]
    [InlineData("high")]
    [InlineData("max")]
    public void Effort(string effort)
        => Assert.Equal(
            [Exe, "--dangerously-skip-permissions", "--effort", effort],
            ClaudeArguments.Build(Exe, "", Opts(effort: effort)));

    [Fact]
    public void Default_effort_adds_no_argument()
        => Assert.DoesNotContain("--effort", ClaudeArguments.Build(Exe, "", Opts(effort: "default")));

    // ---------------- rc ----------------

    [Fact]
    public void Remote_control_is_a_bare_flag()
    {
        var args = ClaudeArguments.Build(Exe, "", Opts(rc: true));
        Assert.Equal([Exe, "--dangerously-skip-permissions", "--remote-control"], args);
        Assert.Equal("--remote-control", args[^1]);   // it takes a value, so always last
    }

    [Fact]
    public void Remote_control_off_has_no_flag()
        => Assert.DoesNotContain("--remote-control", ClaudeArguments.Build(Exe, "", Opts(rc: false)));

    // ---------------- Order ----------------

    [Fact]
    public void Order_with_everything_enabled_is_as_specified()
    {
        // question → perm → session → model → effort → rc (must match the Python version's build_args)
        Assert.Equal(
            [
                Exe, "why so slow?",
                "--permission-mode", "plan",
                "--resume",
                "--model", "opus[1m]",
                "--effort", "max",
                "--remote-control",
            ],
            ClaudeArguments.Build(Exe, "why so slow?", Opts("resume", "opus", "max", "plan", rc: true)));
    }

    [Fact]
    public void Owners_usual_combination()
    {
        // perm=bypass (always) + other defaults + Opus button
        Assert.Equal(
            [Exe, "refactor this", "--dangerously-skip-permissions", "--model", "opus[1m]"],
            ClaudeArguments.Build(Exe, "refactor this", Opts(model: "opus")));
    }

    [Fact]
    public void Default_settings_use_auto_permission_mode()
    {
        // Fresh installs start on auto, not bypass (10-01)
        var args = ClaudeArguments.Build(Exe, "hello", new LaunchOptions());
        Assert.DoesNotContain(ClaudeArguments.BypassFlag, args);
        Assert.Equal(["--permission-mode", "auto"], args.Skip(2).Take(2));
    }

    [Fact]
    public void Empty_executable_path_throws()
    {
        Assert.Throws<ArgumentException>(() => ClaudeArguments.Build("", "hello", Opts()));
        Assert.Throws<ArgumentNullException>(() => ClaudeArguments.Build(null!, "hello", Opts()));
        Assert.Throws<ArgumentNullException>(() => ClaudeArguments.Build(Exe, "hello", null!));
    }

    [Fact]
    public void Display_string_on_one_line()
    {
        // Only items containing spaces are quoted (display only; execution passes the list as is)
        Assert.Equal(
            Exe + " \"why so slow?\" --dangerously-skip-permissions",
            ClaudeArguments.ToDisplayString(ClaudeArguments.Build(Exe, "why so slow?", Opts())));
        Assert.Equal(
            Exe + " hello --dangerously-skip-permissions",
            ClaudeArguments.ToDisplayString(ClaudeArguments.Build(Exe, "hello", Opts())));
    }

    // ---------------- Finding claude ----------------

    [Fact]
    public void Claude_on_PATH_comes_first()
    {
        var path = string.Join(Path.PathSeparator, ["/no/such/dir", "/opt/claude/bin"]);
        Assert.Equal(
            Path.Combine("/opt/claude/bin", "claude.exe"),
            ClaudeExecutable.Resolve(path, "/home/ubuntu", p => p.StartsWith("/opt/claude/bin", StringComparison.Ordinal)));
    }

    [Fact]
    public void Falls_back_to_user_folder_when_not_on_PATH()
        => Assert.Equal(
            Path.Combine("/home/ubuntu", ".local", "bin", "claude.exe"),
            ClaudeExecutable.Resolve("/no/such/dir", "/home/ubuntu", _ => false));

    [Fact]
    public void Empty_PATH_does_not_crash()
    {
        Assert.Equal(
            Path.Combine("/home/ubuntu", ".local", "bin", "claude.exe"),
            ClaudeExecutable.Resolve(null, "/home/ubuntu", _ => false));
        Assert.Equal(
            Path.Combine("/home/ubuntu", ".local", "bin", "claude.exe"),
            ClaudeExecutable.Resolve("", "/home/ubuntu", _ => false));
    }
}
