using IsleBar.Core.Diagnostics;
using Xunit;

namespace IsleBar.Core.Tests;

public class CrashTextTests
{
    [Theory]
    [InlineData(@"Could not find file 'C:\Users\kim\Desktop\secret plan.docx'.", "Could not find file '…'.")]
    [InlineData(@"Access to the path C:\Users\kim\AppData\x.json is denied.", "Access to the path <path>")]
    [InlineData(@"\\server\share\a.txt was locked", "<path> was locked")]
    [InlineData("Window \"Bank — Kim's account\" not found", "Window '…' not found")]
    public void Paths_and_quoted_names_never_survive(string input, string expectedStart)
    {
        var cleaned = CrashText.Clean(input)!;
        Assert.StartsWith(expectedStart, cleaned);
        Assert.DoesNotContain("kim", cleaned, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Long_text_is_cut_and_empty_stays_empty()
    {
        Assert.Equal(CrashText.MaxLength + 1, CrashText.Clean(new string('x', 1000))!.Length);
        Assert.Null(CrashText.Clean(null));
        Assert.Equal("", CrashText.Clean(""));
    }

    [Theory]
    [InlineData(0, false)]            // quit
    [InlineData(3, false)]            // window gone / explorer restarted — handled, not a crash
    [InlineData(4, false)]            // restart requested
    [InlineData(5, false)]            // already running
    [InlineData(1, false)]            // Task Manager / taskkill /F
    [InlineData(-1, false)]           // killed by a script or by setup
    [InlineData(-1073741189, true)]   // 0xC000027B fail-fast
    [InlineData(-1073740940, true)]   // 0xC0000374 heap corruption
    [InlineData(-532462766, false)]   // 0xE0434352 managed crash — already reported with its exception
    public void Only_native_crashes_count(int code, bool report)
        => Assert.Equal(report, CrashText.IsNativeCrash(code));
}
