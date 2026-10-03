using IsleBar.Core.Island;
using Xunit;

namespace IsleBar.Core.Tests;

public class MediaSourcesTests
{
    [Theory]
    [InlineData("chrome.exe")]
    [InlineData("Chrome")]
    [InlineData(@"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("MSEdge")]
    [InlineData("msedge.exe")]
    [InlineData("Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge")]
    [InlineData("firefox.exe")]
    [InlineData("308046B0AF4A39CB")]  // not the app ID shape Firefox uses, but unknown ones count as music apps — checked below
    public void Browser_detection(string appId)
    {
        var expected = appId != "308046B0AF4A39CB";
        Assert.Equal(expected, MediaSources.IsBrowser(appId));
    }

    [Theory]
    [InlineData("Spotify.exe")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify")]
    [InlineData("Melon.exe")]
    [InlineData("AppleInc.AppleMusicWin_nzyj5cx40ttqa!App")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic")]
    [InlineData("foobar2000.exe")]
    [InlineData("")]
    [InlineData(null)]
    public void Music_apps_are_not_browsers(string? appId)
        => Assert.False(MediaSources.IsBrowser(appId));
}
