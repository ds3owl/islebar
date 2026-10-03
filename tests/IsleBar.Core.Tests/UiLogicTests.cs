using IsleBar.Core.Supervisor;
using IsleBar.Core.Ui;
using Xunit;

namespace IsleBar.Core.Tests;

/// <summary>
/// Pure calculations that were buried inside the app (WinUI) are moved to Core and checked here.
/// WinUI can't be built on the server, so before the move these were <b>things we could only learn by going to the PC</b>.
/// </summary>
public sealed class UiLogicTests
{
    // ---------- Choosing the text colour ----------

    [Fact]
    public void Brightness_is_the_average()
    {
        Assert.Equal(0, ThemePalette.Brightness(new Rgb(0, 0, 0)));
        Assert.Equal(255, ThemePalette.Brightness(new Rgb(255, 255, 255)));
        Assert.Equal(100, ThemePalette.Brightness(new Rgb(50, 100, 150)));
    }

    [Theory]
    [InlineData(255, 255, 255, true)]   // white background → dark text
    [InlineData(0, 0, 0, false)]        // black background → light text
    [InlineData(141, 141, 141, true)]   // just above the threshold
    [InlineData(140, 140, 140, false)]  // the threshold itself is not included
    public void Lightness_is_decided_by_actual_background_brightness(byte r, byte g, byte b, bool light)
        => Assert.Equal(light, ThemePalette.IsLightBackground(new Rgb(r, g, b)));

    [Fact]
    public void Light_background_gets_dark_text()
    {
        var accent = new Rgb(0, 0x78, 0xD4);
        var set = ThemePalette.ForBackground(new Rgb(0xF3, 0xF3, 0xF3), accent);

        Assert.True(set.Light);
        Assert.Equal(accent, set.Accent);
        // Text must be darker than the background to be visible — if this flips, the text disappears
        Assert.True(ThemePalette.Brightness(set.Foreground) < ThemePalette.Brightness(new Rgb(0xF3, 0xF3, 0xF3)));
    }

    [Fact]
    public void Default_blue_accent_is_lightened_on_dark_and_kept_on_light()
    {
        var blue = new Rgb(0, 0x78, 0xD4);   // Windows default accent — hard to see on the dark pill (design check 10-01)
        Assert.Equal(blue, ThemePalette.For(true, blue).Accent);
        var dark = ThemePalette.For(false, blue).Accent;
        Assert.True(ThemePalette.Brightness(dark) >= 150);
        Assert.True(dark.B > dark.R);   // still blue, just lighter
        var pale = new Rgb(0xF0, 0xE0, 0x90);
        Assert.True(ThemePalette.Brightness(ThemePalette.For(true, pale).Accent) <= 150);   // too light for the light pill → darkened
        Assert.Equal(pale, ThemePalette.For(false, pale).Accent);
    }

    [Fact]
    public void Dark_background_gets_light_text()
    {
        var bg = new Rgb(0x20, 0x20, 0x20);
        var set = ThemePalette.ForBackground(bg, new Rgb(0, 0x78, 0xD4));

        Assert.False(set.Light);
        Assert.True(ThemePalette.Brightness(set.Foreground) > ThemePalette.Brightness(bg));
    }

    [Fact]
    public void Text_never_blends_into_any_background()
    {
        // Sweep 0–255 and check there's no range where the contrast flips
        for (var v = 0; v <= 255; v += 5)
        {
            var bg = new Rgb((byte)v, (byte)v, (byte)v);
            var set = ThemePalette.ForBackground(bg, new Rgb(0, 0x78, 0xD4));
            var gap = Math.Abs(ThemePalette.Brightness(set.Foreground) - ThemePalette.Brightness(bg));
            Assert.True(gap > 60, $"at brightness {v} text and background are too similar (gap {gap:F0})");
        }
    }

    [Fact]
    public void Accent_colour_is_read_as_reversed_ABGR()
    {
        // 0xFFD47800 → B=0x00, G=0x78, R=0xD4
        var c = ThemePalette.AccentFromDwm(ThemePalette.DefaultAccentValue);
        Assert.Equal(new Rgb(0x00, 0x78, 0xD4), c);
    }

    // ---------- Cutting the pill shape ----------

    [Theory]
    [InlineData(1.0, 2)]     // 1.5 → rounds to 2
    [InlineData(1.25, 2)]    // 1.875 → 2
    [InlineData(1.5, 2)]     // 2.25 → 2
    [InlineData(2.0, 3)]     // 3.0 → 3
    [InlineData(0.5, 2)]     // 0.75 → minimum 2
    public void Inset_follows_scale_with_minimum_of_2(double scale, int expected)
        => Assert.Equal(expected, PillGeometry.EdgeInset(scale));

    [Fact]
    public void Normal_size_gives_a_pill()
    {
        var r = PillGeometry.TryCompute(width: 300, height: 32, scale: 1.0);

        Assert.NotNull(r);
        Assert.Equal(2, r!.Value.Left);
        Assert.Equal(2, r.Value.Top);
        Assert.Equal(299, r.Value.Right);    // 300 - 2 + 1
        Assert.Equal(31, r.Value.Bottom);    // 32 - 2 + 1
        Assert.Equal(28, r.Value.Diameter);  // 32 - 4
    }

    [Theory]
    [InlineData(300, 4)]    // height equals twice the inset → diameter 0
    [InlineData(300, 2)]    // smaller → negative
    [InlineData(4, 32)]     // width equals twice the inset
    [InlineData(0, 0)]
    [InlineData(-10, -10)]
    public void Too_small_is_not_cut(int width, int height)
        => Assert.Null(PillGeometry.TryCompute(width, height, scale: 1.0));

    [Fact]
    public void Diameter_never_goes_negative_at_high_scale()
    {
        // High scale + short window = the combination that broke the old code
        var r = PillGeometry.TryCompute(width: 200, height: 8, scale: 3.0);
        Assert.Null(r);   // inset 5 → diameter -2 → must not cut
    }

    // ---------- Deciding whether to exit on its own ----------

    [Fact]
    public void Does_nothing_when_healthy()
    {
        var v = HealthCheck.Check(windowAlive: true, attachedTaskbar: 10, currentTaskbar: 10,
            startedLight: true, nowLight: true);

        Assert.True(v.Healthy);
        Assert.Equal(HealthProblem.None, v.Problem);
    }

    [Fact]
    public void Window_gone_is_abnormal_exit()
    {
        var v = HealthCheck.Check(false, 10, 10, true, true);

        Assert.Equal(HealthProblem.WindowGone, v.Problem);
        Assert.Equal(ExitCodes.Abnormal, v.ExitCode);
        Assert.True(ExitCodes.ShouldRelaunch(v.ExitCode));
        Assert.Equal(TimeSpan.Zero, v.SettleDelay);
    }

    [Fact]
    public void Explorer_restart_is_abnormal_exit()
    {
        var v = HealthCheck.Check(true, attachedTaskbar: 10, currentTaskbar: 99, startedLight: true, nowLight: true);

        Assert.Equal(HealthProblem.ExplorerRestarted, v.Problem);
        Assert.Equal(ExitCodes.Abnormal, v.ExitCode);
    }

    [Fact]
    public void Different_taskbar_is_ignored_before_attaching()
    {
        // attachedTaskbar 0 = not attached yet. Exiting here would kill it right after launch
        var v = HealthCheck.Check(true, attachedTaskbar: 0, currentTaskbar: 99, startedLight: true, nowLight: true);
        Assert.True(v.Healthy);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Theme_change_restarts_after_waiting_2_seconds(bool started, bool now)
    {
        var v = HealthCheck.Check(true, 10, 10, started, now);

        Assert.Equal(HealthProblem.ThemeChanged, v.Problem);
        Assert.Equal(ExitCodes.RestartRequested, v.ExitCode);
        Assert.Equal(TimeSpan.FromSeconds(2), v.SettleDelay);   // launching before the colours finish changing locks in the wrong colours
    }

    [Fact]
    public void Display_scale_change_restarts_after_waiting()
    {
        var v = HealthCheck.Check(true, 10, 10, true, true, startedDpi: 192, nowDpi: 168);
        Assert.Equal(HealthProblem.DpiChanged, v.Problem);
        Assert.Equal(ExitCodes.RestartRequested, v.ExitCode);
        Assert.Equal(TimeSpan.FromSeconds(2), v.SettleDelay);
    }

    [Theory]
    [InlineData(0u, 168u)]   // DPI unknown at start (no taskbar yet) — nothing to compare
    [InlineData(192u, 0u)]   // taskbar momentarily gone — the Explorer check handles that
    [InlineData(192u, 192u)]
    public void Unknown_or_same_scale_is_healthy(uint started, uint now)
        => Assert.True(HealthCheck.Check(true, 10, 10, true, true, started, now).Healthy);

    [Fact]
    public void Window_gone_takes_precedence_over_theme_change()
    {
        // Even if both apply, a missing window comes first
        var v = HealthCheck.Check(windowAlive: false, 10, 99, startedLight: true, nowLight: false);
        Assert.Equal(HealthProblem.WindowGone, v.Problem);
    }

    [Theory]
    [InlineData(ExitCodes.UserQuit, false)]
    [InlineData(ExitCodes.Abnormal, true)]
    [InlineData(ExitCodes.RestartRequested, true)]
    [InlineData(ExitCodes.AlreadyRunning, false)]
    [InlineData(-1073741189, true)]   // 0xC000027B — died from an exception WinUI couldn't handle
    [InlineData(1, true)]
    public void Relaunches_except_when_quit_or_already_running(int code, bool relaunch)
        => Assert.Equal(relaunch, ExitCodes.ShouldRelaunch(code));
}
