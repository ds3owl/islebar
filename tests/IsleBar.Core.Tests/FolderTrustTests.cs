using Xunit;
using System.Text.Json.Nodes;
using IsleBar.Core.Launch;

namespace IsleBar.Core.Tests;

public class FolderTrustTests
{
    [Fact]
    public void Marks_new_folder_trusted_and_keeps_other_keys()
    {
        using var dir = new TempDir();
        var json = dir.File(".claude.json");
        File.WriteAllText(json, """{"numStartups":5,"projects":{"C:/a":{"hasTrustDialogAccepted":false,"x":1}},"이름":"한글"}""");
        var folder = dir.File("ClaudeBar");

        Assert.True(FolderTrust.Ensure(json, folder));

        var root = JsonNode.Parse(File.ReadAllText(json))!;
        Assert.Equal(5, (int)root["numStartups"]!);
        Assert.Equal("한글", (string)root["이름"]!);
        Assert.Equal(1, (int)root["projects"]!["C:/a"]!["x"]!);
        Assert.True((bool)root["projects"]![FolderTrust.KeyFor(folder)]!["hasTrustDialogAccepted"]!);
        Assert.Contains("한글", File.ReadAllText(json));   // not escaped
    }

    [Fact]
    public void Already_trusted_folder_leaves_file_untouched()
    {
        using var dir = new TempDir();
        var json = dir.File(".claude.json");
        var folder = dir.File("ClaudeBar");
        var text = "{\"projects\":{\"" + FolderTrust.KeyFor(folder) + "\":{\"hasTrustDialogAccepted\":true}}}";
        File.WriteAllText(json, text);
        var before = File.GetLastWriteTimeUtc(json);

        Assert.False(FolderTrust.Ensure(json, folder));
        Assert.Equal(text, File.ReadAllText(json));
        Assert.Equal(before, File.GetLastWriteTimeUtc(json));
    }

    [Fact]
    public void Keeps_other_values_of_existing_entry_and_only_enables_trust()
    {
        using var dir = new TempDir();
        var json = dir.File(".claude.json");
        var folder = dir.File("p");
        File.WriteAllText(json, "{\"projects\":{\"" + FolderTrust.KeyFor(folder) + "\":{\"hasTrustDialogAccepted\":false,\"allowedTools\":[\"a\"]}}}");

        Assert.True(FolderTrust.Ensure(json, folder));
        var p = JsonNode.Parse(File.ReadAllText(json))!["projects"]![FolderTrust.KeyFor(folder)]!;
        Assert.True((bool)p["hasTrustDialogAccepted"]!);
        Assert.Equal("a", (string)p["allowedTools"]![0]!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("{broken")]
    [InlineData("[1,2]")]
    public void Does_nothing_when_file_missing_or_broken(string content)
    {
        using var dir = new TempDir();
        var json = dir.File(".claude.json");
        if (content.Length > 0)
        {
            File.WriteAllText(json, content);
        }

        Assert.False(FolderTrust.Ensure(json, dir.File("p")));
        Assert.Equal(content.Length > 0, File.Exists(json));
        if (content.Length > 0)
        {
            Assert.Equal(content, File.ReadAllText(json));   // never overwrite a broken file
        }
        Assert.False(File.Exists(json + ".islebar.tmp"));
    }

    [Fact]
    public void Key_uses_forward_slashes_without_trailing_slash()
    {
        if (!OperatingSystem.IsWindows()) return;
        Assert.Equal("C:/Users/me/ClaudeBar", FolderTrust.KeyFor(@"C:\Users\me\ClaudeBar\"));
    }

    [Fact]
    public void Codex_folder_is_appended_as_trusted_and_the_rest_is_kept()
    {
        using var dir = new TempDir();
        var toml = dir.File("config.toml");
        const string before = "model = \"gpt\"\r\n[projects.'c:\\users\\me']\r\ntrust_level = \"trusted\"\r\n\r\n[windows]\r\nsandbox = \"elevated\"\r\n";
        File.WriteAllText(toml, before);
        var folder = dir.File("ClaudeBar");

        Assert.True(FolderTrust.EnsureCodex(toml, folder));
        var after = File.ReadAllText(toml);
        Assert.StartsWith(before, after);   // nothing above is changed
        Assert.EndsWith($"\r\n\r\n[projects.'{FolderTrust.CodexKeyFor(folder)}']\r\ntrust_level = \"trusted\"\r\n", after);

        Assert.False(FolderTrust.EnsureCodex(toml, folder));   // second time: already there, file untouched
        Assert.Equal(after, File.ReadAllText(toml));
    }

    [Fact]
    public void Codex_config_with_an_inline_projects_table_is_left_alone()
    {
        using var dir = new TempDir();
        var toml = dir.File("config.toml");
        const string text = "projects = { 'c:\\\\x' = { trust_level = \"trusted\" } }\n";
        File.WriteAllText(toml, text);
        Assert.False(FolderTrust.EnsureCodex(toml, dir.File("ClaudeBar")));   // a [projects.'…'] table here would break the file
        Assert.Equal(text, File.ReadAllText(toml));
    }

    [Fact]
    public void Codex_folder_listed_with_other_casing_or_untrusted_is_left_alone()
    {
        using var dir = new TempDir();
        var toml = dir.File("config.toml");
        var folder = dir.File("ClaudeBar");
        var text = $"[projects.'{FolderTrust.CodexKeyFor(folder).ToUpperInvariant()}']\ntrust_level = \"untrusted\"\n";
        File.WriteAllText(toml, text);

        Assert.False(FolderTrust.EnsureCodex(toml, folder));   // the person's earlier answer stands
        Assert.Equal(text, File.ReadAllText(toml));
    }

    [Fact]
    public void Codex_config_missing_is_not_created()
    {
        using var dir = new TempDir();
        Assert.False(FolderTrust.EnsureCodex(dir.File("config.toml"), dir.File("ClaudeBar")));
        Assert.False(File.Exists(dir.File("config.toml")));
    }
}
