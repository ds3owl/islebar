using IsleBar.Core.Supervisor;
using Xunit;

namespace IsleBar.Core.Tests;

public class RelaunchBackoffTests
{
    [Fact]
    public void Repeated_crashes_double_up_to_60_seconds()
    {
        var b = new RelaunchBackoff();
        var quick = TimeSpan.FromSeconds(2);
        var waits = Enumerable.Range(0, 9).Select(_ => b.After(ExitCodes.Abnormal, quick).TotalSeconds).ToArray();
        Assert.Equal(new double[] { 1, 2, 4, 8, 16, 32, 60, 60, 60 }, waits);
    }

    [Fact]
    public void Restart_request_is_immediate()
        => Assert.Equal(TimeSpan.Zero, new RelaunchBackoff().After(ExitCodes.RestartRequested, TimeSpan.FromSeconds(1)));

    [Fact]
    public void Resets_after_running_well_for_a_while()
    {
        var b = new RelaunchBackoff();
        b.After(ExitCodes.Abnormal, TimeSpan.FromSeconds(1));
        b.After(ExitCodes.Abnormal, TimeSpan.FromSeconds(1));
        Assert.Equal(TimeSpan.FromSeconds(1), b.After(ExitCodes.Abnormal, TimeSpan.FromMinutes(5)));
    }
}
