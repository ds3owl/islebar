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

    /// <summary>A fake Store package: a manifest naming the logos, and PNG files whose headers say <paramref name="widths"/>.</summary>
    private static string Package(TempDir dir, string fullName, params (string File, int Width)[] widths)
    {
        var root = Path.Combine(dir.Path, fullName);
        Directory.CreateDirectory(Path.Combine(root, "Assets"));
        File.WriteAllText(Path.Combine(root, "AppxManifest.xml"),
            """<Package><Properties><Logo>Assets\StoreLogo.png</Logo></Properties>"""
            + """<uap:VisualElements Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png" /></Package>""");
        foreach (var (file, width) in widths)
        {
            var head = new byte[24];
            head[0] = 0x89; head[1] = (byte)'P'; head[2] = (byte)'N'; head[3] = (byte)'G';
            head[16] = (byte)(width >> 24); head[17] = (byte)(width >> 16); head[18] = (byte)(width >> 8); head[19] = (byte)width;
            File.WriteAllBytes(Path.Combine(root, "Assets", file), head);
        }

        return root;
    }

    [Fact]
    public void Store_logo_is_the_widest_unplated_variant_of_the_newest_OpenAI_package()
    {
        using var dir = new TempDir();
        var old = Package(dir, "OpenAI.Codex_1.2025.100.0_x64__2p2nqsd0c76g0", ("Square44x44Logo.targetsize-256.png", 256));
        var current = Package(dir, "OpenAI.Codex_1.2025.300.0_x64__2p2nqsd0c76g0",
            ("Square44x44Logo.scale-200.png", 88),
            ("Square44x44Logo.targetsize-256_altform-unplated.png", 256),
            ("Square150x150Logo.scale-200.png", 300),   // bigger, but the mark is small on its tile
            ("Unrelated.scale-400.png", 1024));

        Assert.Equal(Path.Combine(current, "Assets", "Square44x44Logo.targetsize-256_altform-unplated.png"),
            CodexMark.FindStoreLogo([old, current]));
    }

    [Fact]
    public void No_package_or_no_logo_files_gives_null()
    {
        using var dir = new TempDir();
        var empty = Package(dir, "OpenAI.Codex_1.0.0.0_x64__2p2nqsd0c76g0");
        Assert.Null(CodexMark.FindStoreLogo([empty, Path.Combine(dir.Path, "missing")]));
        Assert.Null(CodexMark.FindStoreLogo([]));
    }

    [Fact]
    public void Manifest_logo_names_are_read()
        => Assert.Equal([@"Assets\Square150x150Logo.png", @"Assets\Square44x44Logo.png", @"Assets\StoreLogo.png"],
            CodexMark.LogoNames("""<Logo>Assets\StoreLogo.png</Logo><x Square150x150Logo="Assets\Square150x150Logo.png" Square44x44Logo="Assets\Square44x44Logo.png"/>""").Order());

    [Theory]
    [InlineData("OpenAI.Codex_1.2025.300.0_x64__2p2nqsd0c76g0", "1.2025.300.0")]
    [InlineData("OpenAI.Codex", "0.0")]
    public void Package_version_is_read_from_the_full_name(string fullName, string version)
        => Assert.Equal(Version.Parse(version), CodexMark.PackageVersionOf(fullName));

    [Theory]
    [InlineData("openai.chatgpt-26.5928.31416-win32-x64", "26.5928.31416")]
    [InlineData("openai.chatgpt-0.4.12", "0.4.12")]
    [InlineData("openai.chatgpt-weird", "0.0")]
    public void Version_is_read_from_the_folder_name(string folder, string version)
        => Assert.Equal(Version.Parse(version), CodexMark.VersionOf(folder));
}
