using IsleBar.Core.Ui;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class SpringCurveTests
{
    [Fact]
    public void Starts_at_0_ends_at_1()
    {
        Assert.Equal(0, SpringCurve.Progress(0, 0.45, 0.7));
        Assert.Equal(1, SpringCurve.Progress(2, 0.45, 0.7), 3);
    }

    [Fact]
    public void Damping_ratio_07_overshoots_slightly_once()
    {
        var peak = Enumerable.Range(0, 1000).Max(i => SpringCurve.Progress(i / 1000.0, 0.45, 0.7));
        Assert.InRange(peak, 1.03, 1.06);   // theoretical e^(−πζ/√(1−ζ²)) ≈ 4.6%
    }

    [Fact]
    public void Critical_damping_does_not_overshoot()
        => Assert.All(Enumerable.Range(0, 2000), i => Assert.True(SpringCurve.Progress(i / 1000.0, 0.4, 1.0) <= 1.0000001));

    [Fact]
    public void Settle_time_depends_on_response_and_damping()
    {
        var pop = SpringCurve.SettleTime(0.45, 0.7);
        Assert.InRange(pop, 0.4, 0.9);
        Assert.True(SpringCurve.SettleTime(0.45, 0.4) > pop);   // less damping wobbles longer
    }

    [Fact]
    public void Samples_go_from_0_to_1()
    {
        var points = SpringCurve.Sample(0.45, 0.7);
        Assert.Equal((0f, 0f), points[0]);
        Assert.Equal((1f, 1f), points[^1]);
        Assert.Equal(41, points.Count);
    }
}
