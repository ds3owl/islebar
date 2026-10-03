using IsleBar.Core.Updates;
using Xunit;

namespace IsleBar.Core.Tests;

public class ReleaseInfoTests
{
    [Theory]
    [InlineData("v0.2.0", "0.2.0")]
    [InlineData("0.3", "0.3.0")]
    [InlineData("IsleBar 1.4.2", "1.4.2")]
    [InlineData("v2", "2.0.0")]
    public void Tags_are_read(string tag, string expected) => Assert.Equal(Version.Parse(expected), ReleaseInfo.ParseTag(tag));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("latest")]
    public void Tags_without_a_version_are_ignored(string? tag) => Assert.Null(ReleaseInfo.ParseTag(tag));

    [Fact]
    public void Latest_release_and_its_page_come_from_the_json()
    {
        var r = ReleaseInfo.ParseLatest("""{"tag_name":"v0.2.0","html_url":"https://github.com/ds3owl/islebar/releases/tag/v0.2.0","draft":false,"prerelease":false}""");
        Assert.Equal(new Version(0, 2, 0), r!.Version);
        Assert.Equal("https://github.com/ds3owl/islebar/releases/tag/v0.2.0", r.Page);
        Assert.Null(r.SetupUrl);
    }

    [Theory]
    [InlineData("""{"tag_name":"v0.9.0","prerelease":true}""")]
    [InlineData("""{"tag_name":"v0.9.0","draft":true}""")]
    [InlineData("""{"message":"Not Found"}""")]
    [InlineData("not json")]
    [InlineData("")]
    public void Drafts_prereleases_and_errors_are_not_offered(string json) => Assert.Null(ReleaseInfo.ParseLatest(json));

    [Fact]
    public void A_page_that_is_not_on_github_is_replaced_by_the_releases_page()
        => Assert.Equal(ReleaseInfo.ReleasesPage, ReleaseInfo.ParseLatest("""{"tag_name":"v1.0.0","html_url":"https://evil.example/x"}""")!.Page);

    [Fact]
    public void Setup_and_its_checksum_are_found_among_the_assets_and_only_on_github()
    {
        var r = ReleaseInfo.ParseLatest("""
            {"tag_name":"v0.2.0","assets":[
              {"name":"IsleBar-Setup-0.2.0.exe","browser_download_url":"https://github.com/ds3owl/islebar/releases/download/v0.2.0/IsleBar-Setup-0.2.0.exe"},
              {"name":"IsleBar-Setup-0.2.0.exe.sha256","browser_download_url":"https://github.com/ds3owl/islebar/releases/download/v0.2.0/IsleBar-Setup-0.2.0.exe.sha256"},
              {"name":"notes.txt","browser_download_url":"https://github.com/x/notes.txt"}]}
            """)!;
        Assert.EndsWith("IsleBar-Setup-0.2.0.exe", r.SetupUrl);
        Assert.EndsWith(".exe.sha256", r.ShaUrl);

        var elsewhere = ReleaseInfo.ParseLatest("""{"tag_name":"v0.2.0","assets":[{"name":"IsleBar-Setup-0.2.0.exe","browser_download_url":"https://evil.example/IsleBar-Setup-0.2.0.exe"}]}""")!;
        Assert.Null(elsewhere.SetupUrl);
    }

    [Fact]
    public void Only_an_exact_checksum_lets_an_update_install()
    {
        var hash = System.Security.Cryptography.SHA256.HashData("setup"u8.ToArray());
        var hex = Convert.ToHexString(hash).ToLowerInvariant();
        Assert.True(ReleaseInfo.HashMatches(hex, hash));
        Assert.True(ReleaseInfo.HashMatches($"{hex}  IsleBar-Setup-0.2.0.exe" + Environment.NewLine, hash));
        Assert.False(ReleaseInfo.HashMatches(hex[..^1] + (hex[^1] == '0' ? "1" : "0"), hash));
        Assert.False(ReleaseInfo.HashMatches("", hash));
        Assert.False(ReleaseInfo.HashMatches(null, hash));
        Assert.False(ReleaseInfo.HashMatches("<html>not found</html>", hash));
    }

    [Fact]
    public void The_checksum_must_belong_to_the_setup_and_come_from_IsleBar()
    {
        const string D = "https://github.com/ds3owl/islebar/releases/download/v0.3.0/";
        var r = ReleaseInfo.ParseLatest("{\"tag_name\":\"v0.3.0\",\"assets\":[" +
            "{\"name\":\"IsleBar-Setup-0.3.0-arm64.exe.sha256\",\"browser_download_url\":\"" + D + "IsleBar-Setup-0.3.0-arm64.exe.sha256\"}," +
            "{\"name\":\"IsleBar-Setup-0.3.0.exe\",\"browser_download_url\":\"" + D + "IsleBar-Setup-0.3.0.exe\"}," +
            "{\"name\":\"IsleBar-Setup-0.3.0.exe.sha256\",\"browser_download_url\":\"" + D + "IsleBar-Setup-0.3.0.exe.sha256\"}]}")!;
        Assert.Equal(D + "IsleBar-Setup-0.3.0.exe", r.SetupUrl);
        Assert.Equal(D + "IsleBar-Setup-0.3.0.exe.sha256", r.ShaUrl);

        var foreign = ReleaseInfo.ParseLatest("{\"tag_name\":\"v0.3.0\",\"assets\":[" +
            "{\"name\":\"IsleBar-Setup-0.3.0.exe\",\"browser_download_url\":\"https://github.com/someone/fork/releases/download/v0.3.0/IsleBar-Setup-0.3.0.exe\"}]}")!;
        Assert.Null(foreign.SetupUrl);
    }

    [Theory]
    [InlineData("0.2.0", "0.1.0", true)]
    [InlineData("0.1.0", "0.1.0", false)]
    [InlineData("0.1", "0.1.0", false)]
    [InlineData("0.1.0", "0.2.0", false)]
    [InlineData("1.0.0", "0.9.9", true)]
    public void Only_a_higher_version_is_newer(string latest, string current, bool newer)
        => Assert.Equal(newer, ReleaseInfo.IsNewer(Version.Parse(latest), Version.Parse(current)));
}
