using IsleBar.Core.Localization;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class BarModesUsableTests
{
    [Fact]
    public void Excluded_modes_are_skipped_by_Tab()
    {
        var usable = BarModes.Usable([BarMode.Claude, BarMode.Files, BarMode.Web], filesOn: true, ["web"]);
        Assert.Equal([BarMode.Claude, BarMode.Files], usable);
        Assert.Equal(BarMode.Claude, BarModes.Next(BarMode.Files, usable, filesOn: true));
    }

    [Fact]
    public void File_search_switch_is_also_considered()
        => Assert.Equal([BarMode.Web], BarModes.Usable([BarMode.Files, BarMode.Web, BarMode.Claude], filesOn: false, ["claude"]));

    [Fact]
    public void Turning_all_off_leaves_only_Claude()
        => Assert.Equal([BarMode.Claude], BarModes.Usable([BarMode.Claude, BarMode.Files, BarMode.Web], filesOn: false, ["claude", "web"]));
}
