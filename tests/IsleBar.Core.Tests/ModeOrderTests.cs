using IsleBar.Core.Configuration;
using IsleBar.Core.Localization;
using Xunit;

namespace IsleBar.Core.Tests;

public class ModeOrderTests
{
    private static readonly BarMode[] WebFirst = [BarMode.Web, BarMode.Claude, BarMode.Files];

    [Theory]
    [InlineData(BarMode.Web, true, BarMode.Claude)]
    [InlineData(BarMode.Claude, true, BarMode.Files)]
    [InlineData(BarMode.Files, true, BarMode.Web)]
    [InlineData(BarMode.Claude, false, BarMode.Web)]   // skipped when file search is off
    public void Cycles_in_configured_order(BarMode now, bool filesOn, BarMode next)
        => Assert.Equal(next, BarModes.Next(now, WebFirst, filesOn));

    [Fact]
    public void First_is_default_mode()
    {
        Assert.Equal(BarMode.Web, BarModes.First(WebFirst, filesOn: true));
        Assert.Equal(BarMode.Claude, BarModes.First([BarMode.Files, BarMode.Claude, BarMode.Web], filesOn: false));
    }

    [Theory]
    [InlineData(new[] { "web", "claude", "files" }, "web,claude,files")]
    [InlineData(new[] { "web" }, "web,claude,files")]                       // missing ones appended in default order
    [InlineData(new[] { "files", "files", "모름", "web" }, "files,web,claude")] // duplicates and unknown names dropped
    [InlineData(new string[0], "claude,files,web")]
    public void Normalizes_saved_order(string[] saved, string expected)
        => Assert.Equal(expected, string.Join(",", BarModes.Normalize(saved).Select(BarModes.Key)));

    [Fact]
    public void Is_saved_to_settings_and_read_back()
    {
        Assert.Equal(["claude", "files", "web"], new IsleBarSettings().ModeOrder);
        var json = new System.Text.Json.Nodes.JsonObject();
        SettingsCodec.Apply(new IsleBarSettings { ModeOrder = ["web", "claude", "files"] }, json);
        Assert.Equal(["web", "claude", "files"], SettingsCodec.FromJson(json).ModeOrder);

        json["mode_order"] = new System.Text.Json.Nodes.JsonArray("files", "files");
        Assert.Equal(["files", "claude", "web"], SettingsCodec.FromJson(json).ModeOrder);
    }
}
