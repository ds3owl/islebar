using IsleBar.Core.Configuration;
using IsleBar.Core.Launch;
using Xunit;

namespace IsleBar.Core.Tests;

/// <summary>
/// The two agent kinds (Claude and Codex): argument assembly, option support, and executable lookup.
///
/// The Codex flag strings themselves can't be verified on this server (CLI not installed).
/// So these tests don't check <b>whether the strings are right</b> but <b>whether the assembly rules are right</b> —
/// ordering, which options get ignored, and whether things follow when a constant changes.
/// If a flag needs fixing, only the constants in <see cref="CodexProfile"/> change and these tests still pass.
/// </summary>
public sealed class AgentProfileTests
{
    private const string ClaudeExe = @"C:\Users\me\.local\bin\claude.exe";
    private const string CodexExe = @"C:\Users\me\AppData\Roaming\npm\codex.cmd";

    private static LaunchOptions Opts(
        string session = "new", string model = "default", string effort = "default",
        string perm = "bypass", bool rc = false)
        => new() { Session = session, Model = model, Effort = effort, Perm = perm, Rc = rc };

    // ---------- Profile lookup ----------

    [Theory]
    [InlineData("claude", AgentKind.Claude)]
    [InlineData("codex", AgentKind.Codex)]
    [InlineData("CODEX", AgentKind.Codex)]      // case-insensitive
    [InlineData("gemini", AgentKind.Claude)]    // unknown value → falls back to Claude
    [InlineData("", AgentKind.Claude)]
    [InlineData(null, AgentKind.Claude)]
    public void Finds_profile_by_name_and_falls_back_to_claude_when_unknown(string? name, string expected)
        => Assert.Equal(expected, AgentProfiles.Get(name).Name);

    [Fact]
    public void There_are_exactly_two_profiles()
        => Assert.Equal([AgentKind.Claude, AgentKind.Codex], AgentProfiles.All.Select(p => p.Name));

    // ---------- Option support ----------

    [Fact]
    public void Claude_uses_all_five_options()
        => Assert.All(LaunchOptionDefs.All, o => Assert.True(ClaudeProfile.Instance.Supports(o)));

    [Theory]
    [InlineData(LaunchOptionDefs.Session, true)]
    [InlineData(LaunchOptionDefs.Model, true)]
    [InlineData(LaunchOptionDefs.Perm, true)]
    [InlineData(LaunchOptionDefs.Effort, false)]  // the model name carries its character
    [InlineData(LaunchOptionDefs.Rc, false)]      // Codex has no such feature
    public void Codex_does_not_use_effort_or_remote_control(string option, bool expected)
        => Assert.Equal(expected, CodexProfile.Instance.Supports(option));

    [Fact]
    public void Only_claude_auto_updates_its_model_list()
    {
        Assert.True(ClaudeProfile.Instance.SupportsModelAutoUpdate);
        Assert.False(CodexProfile.Instance.SupportsModelAutoUpdate);
    }

    [Fact]
    public void Codex_default_models_are_full_ids_not_short_names()
    {
        // Short names (sol, astra) actually fail — they must not be in the list
        Assert.DoesNotContain("sol", CodexProfile.Models);
        Assert.DoesNotContain("astra", CodexProfile.Models);
        Assert.Contains("gpt-5.6-sol", CodexProfile.Models);
        Assert.All(CodexProfile.Models, m => Assert.StartsWith("gpt-", m));
    }

    // ---------- Claude profile = existing behaviour unchanged ----------

    [Fact]
    public void Claude_profile_gives_same_result_as_existing_assembly()
    {
        var opts = Opts(session: "resume", model: "opus", effort: "high", perm: "bypass", rc: true);
        Assert.Equal(
            ClaudeArguments.Build(ClaudeExe, "왜 이렇게 됐지", opts),
            ClaudeProfile.Instance.BuildArgs(ClaudeExe, "왜 이렇게 됐지", opts));
    }

    // ---------- Codex assembly ----------

    [Fact]
    public void Codex_puts_question_last()
    {
        var args = CodexProfile.Instance.BuildArgs(CodexExe, "이 함수 고쳐 줘", Opts(perm: "ask"));
        Assert.Equal([CodexExe, "이 함수 고쳐 줘"], args);
    }

    [Fact]
    public void Codex_without_question_has_no_question_slot()
    {
        Assert.Equal([CodexExe], CodexProfile.Instance.BuildArgs(CodexExe, null, Opts(perm: "ask")));
        Assert.Equal([CodexExe], CodexProfile.Instance.BuildArgs(CodexExe, "   ", Opts(perm: "ask")));
    }

    [Theory]
    [InlineData("-v 왜 이래")]
    [InlineData("--help 말고")]
    public void Codex_also_protects_hyphen_question_with_leading_space(string question)
    {
        var args = CodexProfile.Instance.BuildArgs(CodexExe, question, Opts(perm: "ask"));
        Assert.Equal(" " + question, args[^1]);
    }

    [Theory]
    [InlineData("bypass", CodexProfile.BypassFlag)]
    [InlineData("auto", CodexProfile.FullAutoFlag)]
    public void Codex_permission_mapping(string perm, string expected)
    {
        var args = CodexProfile.Instance.BuildArgs(CodexExe, "질문", Opts(perm: perm));
        Assert.Equal([CodexExe, expected, "질문"], args);
    }

    [Theory]
    [InlineData("ask")]
    [InlineData("plan")]   // Codex has no equivalent → nothing is added
    public void Codex_adds_nothing_when_permission_has_no_equivalent(string perm)
        => Assert.Equal([CodexExe, "질문"], CodexProfile.Instance.BuildArgs(CodexExe, "질문", Opts(perm: perm)));

    [Fact]
    public void Codex_resume_is_a_subcommand_right_after_executable()
    {
        Assert.Equal(
            [CodexExe, CodexProfile.ResumeCommand, CodexProfile.ResumeLastFlag, "질문"],
            CodexProfile.Instance.BuildArgs(CodexExe, "질문", Opts(session: "continue", perm: "ask")));

        // the picker: the first word after "resume" is a session id, so the question is not passed (review 10-03)
        Assert.Equal(
            [CodexExe, CodexProfile.ResumeCommand],
            CodexProfile.Instance.BuildArgs(CodexExe, "질문", Opts(session: "resume", perm: "ask")));
    }

    [Fact]
    public void One_word_questions_that_are_subcommands_stay_questions()
    {
        Assert.Equal([CodexExe, " logout"], CodexProfile.Instance.BuildArgs(CodexExe, "logout", Opts(session: "new", perm: "ask")));
        Assert.Equal([CodexExe, "logout please"], CodexProfile.Instance.BuildArgs(CodexExe, "logout please", Opts(session: "new", perm: "ask")));
        Assert.True(ClaudeArguments.IsSubcommand("update"));
        Assert.True(ClaudeArguments.IsSubcommand(" Doctor "));
        Assert.False(ClaudeArguments.IsSubcommand("update the readme"));
    }

    [Fact]
    public void Codex_new_session_has_no_subcommand()
        => Assert.Equal([CodexExe, "질문"], CodexProfile.Instance.BuildArgs(CodexExe, "질문", Opts(session: "new", perm: "ask")));

    [Fact]
    public void Codex_passes_model_full_id_as_is()
    {
        // No substitution like Claude's opus→opus[1m]
        Assert.Equal(
            [CodexExe, CodexProfile.ModelFlag, "gpt-6-astra", "질문"],
            CodexProfile.Instance.BuildArgs(CodexExe, "질문", Opts(model: "gpt-6-astra", perm: "ask")));
    }

    [Fact]
    public void Codex_adds_no_model_when_default()
        => Assert.Equal([CodexExe, "질문"], CodexProfile.Instance.BuildArgs(CodexExe, "질문", Opts(model: "default", perm: "ask")));

    [Fact]
    public void Codex_ignores_effort_and_remote_control_even_when_set()
    {
        var args = CodexProfile.Instance.BuildArgs(
            CodexExe, "질문", Opts(effort: "max", rc: true, perm: "ask"));
        Assert.Equal([CodexExe, "질문"], args);
        Assert.DoesNotContain("--effort", args);
        Assert.DoesNotContain("--remote-control", args);
    }

    [Fact]
    public void Codex_order_with_everything_enabled()
    {
        var args = CodexProfile.Instance.BuildArgs(
            CodexExe, "질문", Opts(session: "continue", model: "gpt-5.5", effort: "high", perm: "bypass", rc: true));

        // subcommand → permission → model → question
        Assert.Equal(
            [
                CodexExe,
                CodexProfile.ResumeCommand, CodexProfile.ResumeLastFlag,
                CodexProfile.BypassFlag,
                CodexProfile.ModelFlag, "gpt-5.5",
                "질문",
            ],
            args);
    }

    // ---------- Executable lookup ----------

    [Fact]
    public void Uses_the_one_on_PATH_if_present()
    {
        // The PATH separator differs by OS (Windows ';' / Linux ':').
        // Splitting on ':' on Linux would cut a path like "C:\tools" in two,
        // so this test only uses names unaffected by separators or drive letters.
        var found = Path.Combine("tools", CodexProfile.WindowsExecutable);
        var searchPath = string.Join(Path.PathSeparator, ["other", "tools"]);
        Assert.Equal(found, ClaudeExecutable.Resolve(
            CodexProfile.Instance, searchPath, "home", p => p == found));
    }

    [Fact]
    public void Checks_fallback_locations_in_order_when_not_on_PATH()
    {
        var npm = Path.Combine(@"C:\Users\me", "AppData", "Roaming", "npm", CodexProfile.WindowsExecutable);
        Assert.Equal(npm, ClaudeExecutable.Resolve(
            CodexProfile.Instance, "", @"C:\Users\me", p => p == npm));
    }

    [Fact]
    public void Finds_an_npm_installed_claude_and_a_native_codex()
    {
        if (!OperatingSystem.IsWindows()) return;   // Windows path rules
        var claudeCmd = Path.Combine(@"C:\Users\me", "AppData", "Roaming", "npm", "claude.cmd");
        Assert.Equal(claudeCmd, ClaudeExecutable.Resolve(ClaudeProfile.Instance, "", @"C:\Users\me", p => p == claudeCmd));
        var codexExe = Path.Combine("tools", "codex.exe");
        Assert.Equal(codexExe, ClaudeExecutable.Resolve(CodexProfile.Instance, "tools", "home", p => p == codexExe));
        // the native Claude is still preferred, and still the guess when nothing is found
        Assert.Equal(Path.Combine(@"C:\Users\me", ".local", "bin", "claude.exe"),
            ClaudeExecutable.Resolve(ClaudeProfile.Instance, "", @"C:\Users\me", _ => false));
    }

    [Fact]
    public void Returns_first_fallback_location_when_found_nowhere()
    {
        var expected = Path.Combine(@"C:\Users\me", "AppData", "Roaming", "npm", CodexProfile.WindowsExecutable);
        Assert.Equal(expected, ClaudeExecutable.Resolve(
            CodexProfile.Instance, @"C:\nope", @"C:\Users\me", _ => false));
    }

    [Fact]
    public void On_linux_looks_for_name_without_extension()
    {
        if (OperatingSystem.IsWindows()) return; // path separators follow the running OS, so this is only meaningful on Linux
        var found = "/usr/local/bin/codex";
        Assert.Equal(found, ClaudeExecutable.Resolve(
            CodexProfile.Instance, "/usr/local/bin", "/home/ubuntu", p => p == found, windows: false));
    }

    // ---------- Settings ----------

    [Fact]
    public void Settings_model_lists_are_separate_per_agent()
    {
        var s = new IsleBarSettings();
        Assert.Equal(s.Models, s.ModelsFor(AgentKind.Claude));
        Assert.Equal(s.CodexModels, s.ModelsFor(AgentKind.Codex));
        Assert.NotEqual(s.Models, s.CodexModels);
    }

    [Fact]
    public void Settings_default_agent_is_claude()
        => Assert.Equal(AgentKind.Claude, new IsleBarSettings().Agent);

    [Fact]
    public void Settings_clone_copies_agent_and_codex_models()
    {
        var s = new IsleBarSettings { Agent = AgentKind.Codex };
        s.CodexModels.Add("gpt-7-nova");

        var copy = s.Clone();
        Assert.Equal(AgentKind.Codex, copy.Agent);
        Assert.Contains("gpt-7-nova", copy.CodexModels);

        copy.CodexModels.Add("딴것");
        Assert.DoesNotContain("딴것", s.CodexModels);   // the list must not be shared
    }
}
