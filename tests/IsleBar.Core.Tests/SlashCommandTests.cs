using IsleBar.Core.Commands;
using Xunit;

namespace IsleBar.Core.Tests;

/// <summary>
/// Scanning and picking slash commands.
///
/// Owner's report (2026-09-29): commands from the bar "don't take effect right away". Measuring showed the arguments were being passed
/// (<c>claude -p "/help"</c> answered "not available in this environment" = recognized as a command);
/// the real problem was that <b>when the working folder isn't that project, project commands simply don't exist</b>.
/// So we build a list showing only "what can actually be used in this folder right now".
/// </summary>
public sealed class SlashCommandTests
{
    private static void Write(string path, string text)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    private static string CommandFile(string root, string scope, params string[] parts)
        => Path.Combine([root, scope, ".claude", "commands", .. parts]);

    // ---------- Scanning ----------

    [Fact]
    public void Looks_at_both_working_folder_and_home()
    {
        using var t = new TempDir();
        Write(CommandFile(t.Path, "proj", "deploy.md"), "배포한다");
        Write(CommandFile(t.Path, "home", "note.md"), "메모한다");

        var found = SlashCommandScanner.Scan(Path.Combine(t.Path, "proj"), Path.Combine(t.Path, "home"));

        Assert.Equal(["deploy", "note"], found.Select(c => c.Name));
        Assert.Equal(CommandSource.Project, found[0].Source);
        Assert.Equal(CommandSource.User, found[1].Source);
    }

    [Fact]
    public void No_project_commands_without_working_folder()
    {
        using var t = new TempDir();
        Write(CommandFile(t.Path, "proj", "deploy.md"), "배포한다");
        Write(CommandFile(t.Path, "home", "note.md"), "메모한다");

        // This is what the owner ran into — launched from another folder, project commands disappear
        var found = SlashCommandScanner.Scan(null, Path.Combine(t.Path, "home"));

        Assert.Equal(["note"], found.Select(c => c.Name));
    }

    [Fact]
    public void Missing_folders_give_empty_list_without_crashing()
    {
        using var t = new TempDir();
        Assert.Empty(SlashCommandScanner.Scan(Path.Combine(t.Path, "없음"), Path.Combine(t.Path, "없음2")));
        Assert.Empty(SlashCommandScanner.Scan(null, null));
        Assert.Empty(SlashCommandScanner.Scan("", "   "));
    }

    [Fact]
    public void Subfolders_are_joined_with_colon()
    {
        using var t = new TempDir();
        Write(CommandFile(t.Path, "home", "git", "commit.md"), "커밋한다");

        var found = SlashCommandScanner.Scan(null, Path.Combine(t.Path, "home"));

        Assert.Equal("git:commit", found[0].Name);
        Assert.Equal("/git:commit", found[0].Typed);
    }

    [Fact]
    public void Plugin_commands_are_prefixed_with_plugin_name()
    {
        using var t = new TempDir();
        var home = Path.Combine(t.Path, "home");
        Write(Path.Combine(home, ".claude", "plugins", "synced", "myplug", "commands", "run.md"), "돌린다");

        var found = SlashCommandScanner.Scan(null, home);

        Assert.Equal("myplug:run", found[0].Name);
        Assert.Equal(CommandSource.Plugin, found[0].Source);
    }

    [Fact]
    public void Project_wins_on_name_clash()
    {
        using var t = new TempDir();
        Write(CommandFile(t.Path, "proj", "build.md"), "---\ndescription: 프로젝트 것\n---\n");
        Write(CommandFile(t.Path, "home", "build.md"), "---\ndescription: 홈 것\n---\n");

        var found = SlashCommandScanner.Scan(Path.Combine(t.Path, "proj"), Path.Combine(t.Path, "home"));

        Assert.Single(found);
        Assert.Equal(CommandSource.Project, found[0].Source);
        Assert.Equal("프로젝트 것", found[0].Description);
    }

    [Fact]
    public void Returns_sorted_by_name()
    {
        using var t = new TempDir();
        foreach (var n in new[] { "zeta", "alpha", "Mid" })
        {
            Write(CommandFile(t.Path, "home", n + ".md"), "x");
        }

        var found = SlashCommandScanner.Scan(null, Path.Combine(t.Path, "home"));
        Assert.Equal(["alpha", "Mid", "zeta"], found.Select(c => c.Name));
    }

    [Fact]
    public void Ignores_non_md_files()
    {
        using var t = new TempDir();
        Write(CommandFile(t.Path, "home", "real.md"), "x");
        Write(CommandFile(t.Path, "home", "readme.txt"), "x");
        Write(CommandFile(t.Path, "home", "script.sh"), "x");

        Assert.Equal(["real"], SlashCommandScanner.Scan(null, Path.Combine(t.Path, "home")).Select(c => c.Name));
    }

    // ---------- Reading front matter ----------

    [Theory]
    [InlineData("---\ndescription: 코드 본다\nargument-hint: <PR번호>\n---\n본문", "코드 본다", "<PR번호>")]
    [InlineData("---\ndescription: \"따옴표 붙은 것\"\n---\n", "따옴표 붙은 것", "")]
    [InlineData("---\ndescription: '홑따옴표'\n---\n", "홑따옴표", "")]
    [InlineData("---\nDescription: 대문자 키\n---\n", "대문자 키", "")]
    [InlineData("머리말이 아예 없다", "", "")]
    [InlineData("---\n엉뚱한 줄\n---\n", "", "")]
    [InlineData("", "", "")]
    public void Extracts_only_description_and_argument_hint_from_front_matter(string text, string desc, string hint)
    {
        using var t = new TempDir();
        var path = CommandFile(t.Path, "home", "c.md");
        Write(path, text);

        var found = SlashCommandScanner.Scan(null, Path.Combine(t.Path, "home"));
        Assert.Equal(desc, found[0].Description);
        Assert.Equal(hint, found[0].ArgumentHint);
    }

    [Fact]
    public void Command_stays_listed_even_with_broken_front_matter()
    {
        using var t = new TempDir();
        Write(CommandFile(t.Path, "home", "broken.md"), "---\n닫지 않은 머리말\n설명도 없음\n");

        var found = SlashCommandScanner.Scan(null, Path.Combine(t.Path, "home"));

        Assert.Single(found);
        Assert.Equal("broken", found[0].Name);   // the name is still usable, so it isn't dropped
    }

    [Fact]
    public void Commands_taking_arguments_show_the_argument_in_display()
    {
        var c = new SlashCommand("code-review", CommandSource.User, "본다", "<PR번호>", "x");
        Assert.Equal("/code-review <PR번호>", c.Display);

        var noArg = new SlashCommand("simplify", CommandSource.User, "줄인다", "", "x");
        Assert.Equal("/simplify", noArg.Display);
    }

    // ---------- Picking ----------

    private static readonly IReadOnlyList<SlashCommand> Sample =
    [
        new("code-review", CommandSource.User, "코드를 본다", "<PR번호>", "a"),
        new("commit", CommandSource.Project, "커밋한다", "", "b"),
        new("loop", CommandSource.User, "되풀이한다", "<간격>", "c"),
        new("simplify", CommandSource.User, "코드를 줄인다", "", "d"),
    ];

    [Theory]
    [InlineData("/co", true)]
    [InlineData("  /co", true)]
    [InlineData("/", true)]
    [InlineData("왜 이래", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Knows_when_input_is_a_command(string? text, bool expected)
        => Assert.Equal(expected, SlashCommandMatcher.IsCommandInput(text));

    [Theory]
    [InlineData("/loop 5m /foo", "loop")]
    [InlineData("/commit", "commit")]
    [InlineData("/", "")]
    [InlineData("그냥 질문", null)]
    public void Extracts_only_the_name_part(string text, string? expected)
        => Assert.Equal(expected, SlashCommandMatcher.NamePart(text));

    [Fact]
    public void Slash_alone_shows_from_the_top()
        => Assert.Equal(
            ["code-review", "commit", "loop", "simplify"],
            SlashCommandMatcher.Match(Sample, "/").Select(c => c.Name));

    [Fact]
    public void Prefix_matches_come_first()
    {
        // "co" is a prefix of code-review and commit, while simplify only has it in its description ("코드를 줄인다")
        var m = SlashCommandMatcher.Match(Sample, "/co");
        Assert.Equal(["code-review", "commit"], m.Take(2).Select(c => c.Name));
    }

    [Fact]
    public void Falls_back_to_description_when_not_in_name()
    {
        var m = SlashCommandMatcher.Match(Sample, "/되풀이");
        Assert.Equal(["loop"], m.Select(c => c.Name));
    }

    [Fact]
    public void No_match_gives_empty_list()
        => Assert.Empty(SlashCommandMatcher.Match(Sample, "/없는명령어xyz"));

    [Fact]
    public void Non_command_input_gives_empty_list()
        => Assert.Empty(SlashCommandMatcher.Match(Sample, "왜 이렇게 됐지"));

    [Fact]
    public void While_typing_arguments_only_that_command_remains()
    {
        var m = SlashCommandMatcher.Match(Sample, "/loop 5m");
        Assert.Single(m);
        Assert.Equal("loop", m[0].Name);
    }

    [Fact]
    public void Typing_arguments_with_wrong_name_gives_empty_list()
        => Assert.Empty(SlashCommandMatcher.Match(Sample, "/없는것 인자"));

    [Fact]
    public void Limits_the_count()
        => Assert.Equal(2, SlashCommandMatcher.Match(Sample, "/", limit: 2).Count);

    [Fact]
    public void Picking_adds_trailing_space_only_for_commands_taking_arguments()
    {
        Assert.Equal("/loop ", SlashCommandMatcher.Complete(Sample[2]));
        Assert.Equal("/commit", SlashCommandMatcher.Complete(Sample[1]));
    }

    [Fact]
    public void Case_insensitive()
        => Assert.Equal(["code-review"], SlashCommandMatcher.Match(Sample, "/CODE").Select(c => c.Name));
}
