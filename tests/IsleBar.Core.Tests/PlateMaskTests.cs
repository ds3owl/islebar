using IsleBar.Core.Ui;
using Xunit;

namespace IsleBar.Core.Tests;

public class PlateMaskTests
{
    /// <summary>A 100×100 picture: a 40×40 dark square in the middle on <paramref name="plate"/> (null = transparent).</summary>
    private static byte[] Picture((byte B, byte G, byte R)? plate)
    {
        var bgra = new byte[100 * 100 * 4];
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                var i = ((y * 100) + x) * 4;
                var mark = x >= 30 && x < 70 && y >= 30 && y < 70;
                if (mark)
                {
                    bgra[i] = 40; bgra[i + 1] = 40; bgra[i + 2] = 50; bgra[i + 3] = 255;
                }
                else if (plate is { } p)
                {
                    bgra[i] = p.B; bgra[i + 1] = p.G; bgra[i + 2] = p.R; bgra[i + 3] = 255;
                }
            }
        }

        return bgra;
    }

    [Fact]
    public void Transparent_background_keeps_the_opaque_shape_centered()
    {
        var mask = PlateMask.Build(Picture(null), 100, 100, 22);
        Assert.NotNull(mask);
        Assert.Equal(255, mask![(11 * 22) + 11]);   // middle is mark
        Assert.Equal(0, mask[0]);                   // corner is margin
    }

    [Fact]
    public void Plated_logo_keeps_what_differs_from_the_plate()
    {
        var mask = PlateMask.Build(Picture((255, 255, 255)), 100, 100, 22);
        Assert.NotNull(mask);
        Assert.Equal(255, mask![(11 * 22) + 11]);
        Assert.Equal(0, mask[0]);
    }

    [Fact]
    public void A_bare_mark_may_fill_its_whole_picture()
    {
        // a transparent ring touching all four edges (an "unplated" app icon drawn edge to edge)
        var bgra = new byte[100 * 100 * 4];
        for (var y = 0; y < 100; y++)
        {
            for (var x = 0; x < 100; x++)
            {
                var d = Math.Sqrt(((x - 49.5) * (x - 49.5)) + ((y - 49.5) * (y - 49.5)));
                bgra[(((y * 100) + x) * 4) + 3] = d is > 35 and < 50 ? (byte)255 : (byte)0;
            }
        }

        var mask = PlateMask.Build(bgra, 100, 100, 22);
        Assert.NotNull(mask);
        Assert.Equal(0, mask![(11 * 22) + 11]);   // the hole stays clear
    }

    [Fact]
    public void A_bare_plate_or_empty_picture_gives_null()
    {
        var plain = new byte[100 * 100 * 4];
        for (var i = 0; i < plain.Length; i += 4)
        {
            plain[i] = plain[i + 1] = plain[i + 2] = plain[i + 3] = 255;
        }

        Assert.Null(PlateMask.Build(plain, 100, 100, 22));
        Assert.Null(PlateMask.Build(new byte[100 * 100 * 4], 100, 100, 22));
        Assert.Null(PlateMask.Build(new byte[10], 100, 100, 22));
    }
}
