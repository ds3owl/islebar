using IsleBar.Core.Ui;
using Xunit;

namespace IsleBar.Core.Tests;

public class GlyphMaskTests
{
    private static byte[] Image(int w, int h, Func<int, int, (byte B, byte G, byte R, byte A)> pixel)
    {
        var data = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (b, g, r, a) = pixel(x, y);
                var i = ((y * w) + x) * 4;
                (data[i], data[i + 1], data[i + 2], data[i + 3]) = (b, g, r, a);
            }
        }

        return data;
    }

    [Fact]
    public void Only_orange_shape_remains_in_center_and_outside_is_transparent()
    {
        // Orange square centred on an opaque white background — white isn't orange, so it must drop out
        var img = Image(64, 64, (x, y) => x is >= 20 and < 44 && y is >= 20 and < 44
            ? ((byte)80, (byte)120, (byte)217, (byte)255)
            : ((byte)255, (byte)255, (byte)255, (byte)255));

        var mask = GlyphMask.Build(img, 64, 64, 16)!;

        Assert.Equal(16 * 16, mask.Length);
        Assert.True(mask[(8 * 16) + 8] > 200);   // centre is solid
        Assert.Equal(0, mask[0]);                  // corners are transparent (white background doesn't leak in)
        Assert.Equal(0, mask[(15 * 16) + 15]);
    }

    [Fact]
    public void No_orange_gives_null()
    {
        var img = Image(8, 8, (_, _) => ((byte)200, (byte)200, (byte)200, (byte)255));
        Assert.Null(GlyphMask.Build(img, 8, 8, 16));
    }

    [Theory]
    [InlineData(80, 120, 217, 255, true)]    // Claude orange
    [InlineData(80, 120, 217, 0, false)]     // transparent
    [InlineData(192, 192, 255, 1, false)]    // stray colour on a nearly transparent edge (real claude.exe icon)
    [InlineData(200, 200, 217, 255, false)]  // red not sufficiently above blue (whitish)
    [InlineData(20, 20, 120, 255, false)]    // dark red
    public void Orange_detection(byte b, byte g, byte r, byte a, bool expected)
        => Assert.Equal(expected, GlyphMask.IsOrange(b, g, r, a));

    [Fact]
    public void Invalid_size_gives_null()
    {
        Assert.Null(GlyphMask.Build(new byte[16], 2, 2, 0));
        Assert.Null(GlyphMask.Build(new byte[4], 2, 2, 8));
    }

    [Theory]
    [InlineData(IsleBar.Core.Localization.BarMode.Claude, "claude", "Ask Claude")]
    [InlineData(IsleBar.Core.Localization.BarMode.Claude, "codex", "Ask Codex")]
    [InlineData(IsleBar.Core.Localization.BarMode.Files, "codex", "Search files")]
    [InlineData(IsleBar.Core.Localization.BarMode.Claude, null, "Ask Claude")]
    public void Placeholder_follows_agent(IsleBar.Core.Localization.BarMode mode, string? agent, string expected)
        => Assert.Equal(expected, IsleBar.Core.Localization.Placeholders.For(mode, agent));
}
