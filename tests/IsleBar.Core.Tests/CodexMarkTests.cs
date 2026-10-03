using IsleBar.Core.Ui;
using Xunit;

namespace IsleBar.Core.Tests;

public class CodexMarkTests
{
    private static string Ext(TempDir dir, string root, string folder, params string[] files)
    {
        var res = Path.Combine(dir.Path, root, folder, "resources");
        Directory.CreateDirectory(res);
        foreach (var f in files)
        {
            File.WriteAllText(Path.Combine(res, f), "<svg/>");
        }

        return Path.Combine(dir.Path, root);
    }

    [Fact]
    public void Picks_the_theme_variant_from_the_newest_extension_across_editors()
    {
        using var dir = new TempDir();
        var vscode = Ext(dir, "vscode", "openai.chatgpt-26.917.1-win32-x64", "blossom-black.svg", "blossom-white.svg");
        var cursor = Ext(dir, "cursor", "openai.chatgpt-26.5928.31416", "blossom-black.svg", "blossom-white.svg");
        Ext(dir, "vscode", "someone.else-99.0.0", "blossom-black.svg");

        var roots = new[] { vscode, cursor, Path.Combine(dir.Path, "missing") };
        Assert.Equal(Path.Combine(cursor, "openai.chatgpt-26.5928.31416", "resources", "blossom-black.svg"), CodexMark.Find(roots, light: true));
        Assert.EndsWith(Path.Combine("openai.chatgpt-26.5928.31416", "resources", "blossom-white.svg"), CodexMark.Find(roots, light: false));
    }

    [Fact]
    public void Nothing_installed_or_file_missing_gives_null()
    {
        using var dir = new TempDir();
        var root = Ext(dir, "vscode", "openai.chatgpt-1.0.0", "blossom.dark.png");
        Assert.Null(CodexMark.Find([root], light: true));
        Assert.Null(CodexMark.Find([], light: false));
    }

    [Theory]
    [InlineData("openai.chatgpt-26.5928.31416-win32-x64", "26.5928.31416")]
    [InlineData("openai.chatgpt-0.4.12", "0.4.12")]
    [InlineData("openai.chatgpt-weird", "0.0")]
    public void Version_is_read_from_the_folder_name(string folder, string version)
        => Assert.Equal(Version.Parse(version), CodexMark.VersionOf(folder));
}
