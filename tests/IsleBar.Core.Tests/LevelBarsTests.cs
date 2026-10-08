using IsleBar.Core.Ui;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class LevelBarsTests
{
    [Fact]
    public void Silence_rests_at_the_floor()
    {
        var bars = new LevelBars();
        for (var i = 0; i < 20; i++)
        {
            bars.Update([0f, 0f, 0f, 0f]);
        }

        Assert.All(bars.Heights, h => Assert.Equal(LevelBars.Floor, h, 3));
    }

    [Fact]
    public void A_beat_rises_fast_and_falls_slowly()
    {
        var bars = new LevelBars();
        for (var i = 0; i < 60; i++)
        {
            bars.Update([0.01f, 0.01f, 0.01f, 0.01f]);   // quiet stretch sets the low envelope
        }

        var up = bars.Update([0.08f, 0.01f, 0.01f, 0.01f]).ToArray();   // a kick drum
        Assert.True(up[0] > 0.7f, $"{up[0]}");
        Assert.True(up[1] < 0.5f, "other bands stay down");

        var down = bars.Update([0.01f, 0.01f, 0.01f, 0.01f]);
        Assert.True(down[0] > LevelBars.Floor + ((up[0] - LevelBars.Floor) * 0.5f), "fell too fast");
    }

    [Fact]
    public void Quiet_playback_still_uses_the_full_height()
    {
        var bars = new LevelBars();
        float max = 0f, min = 1f;
        for (var i = 0; i < 120; i++)
        {
            var v = 0.002f + (0.002f * MathF.Sin(i * 0.7f));   // very low volume
            var h = bars.Update([v, v, v, v]);
            if (i > 40)
            {
                max = Math.Max(max, h[0]);
                min = Math.Min(min, h[0]);
            }
        }

        Assert.True(max > 0.8f, $"max {max}");
        Assert.True(max - min > 0.4f, $"range {max - min}");
    }

    [Fact]
    public void Bars_move_again_soon_after_a_loud_burst()
    {
        // 10-08: after one loud burst (notification sound) the loud envelope held the bars on the floor for seconds
        var bars = new LevelBars();
        float Wave(int i) => 0.002f + (0.0015f * MathF.Sin(i * 0.7f));
        for (var i = 0; i < 90; i++)
        {
            bars.Update([Wave(i), Wave(i), Wave(i), Wave(i)]);
        }

        for (var i = 0; i < 3; i++)
        {
            bars.Update([0.04f, 0.04f, 0.04f, 0.04f]);   // 20x louder for 0.1 s
        }

        float max = 0f;
        for (var i = 0; i < 45; i++)   // 1.5 s later
        {
            var h = bars.Update([Wave(i), Wave(i), Wave(i), Wave(i)]);
            if (i >= 30)
            {
                max = Math.Max(max, h[0]);
            }
        }

        Assert.True(max > 0.35f, $"still stuck near the floor: {max}");
    }

    [Fact]
    public void Heights_stay_in_range()
    {
        var bars = new LevelBars();
        foreach (var v in new[] { 2f, -1f, 1f, 0.5f })
        {
            Assert.All(bars.Update([v, v, v, v]), h => Assert.InRange(h, LevelBars.Floor, 1f));
        }
    }

    [Theory]
    [InlineData(80, 0)]
    [InlineData(500, 1)]
    [InlineData(2000, 2)]
    [InlineData(9000, 3)]
    public void Band_meter_puts_a_tone_in_its_band(int hz, int band)
    {
        var meter = new BandMeter(48000);
        for (var i = 0; i < 48000 / 4; i++)
        {
            meter.Add(MathF.Sin(2 * MathF.PI * hz * i / 48000f));
        }

        var levels = meter.TakeLevels();
        Assert.Equal(band, Array.IndexOf(levels, levels.Max()));
    }
}
