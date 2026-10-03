using IsleBar.Core.Ui;
using Xunit;

namespace IsleBar.Core.Tests;

public sealed class AlbumTintTests
{
    private static byte[] Fill(int count, byte r, byte g, byte b)
        => Enumerable.Range(0, count).SelectMany(_ => new[] { b, g, r, (byte)255 }).ToArray();

    [Fact]
    public void Grayscale_album_gets_no_tint()
        => Assert.Null(AlbumTint.Pick(Fill(64, 120, 120, 120), lightBackground: true));

    [Fact]
    public void Red_subject_on_white_background_gives_red()
    {
        var pixels = Fill(200, 250, 250, 250).Concat(Fill(30, 220, 30, 40)).ToArray();
        var tint = AlbumTint.Pick(pixels, lightBackground: true)!.Value;
        Assert.True(tint.R > tint.G * 2 && tint.R > tint.B * 2, $"{tint}");
    }

    [Fact]
    public void Not_too_bright_on_light_background_and_not_too_dark_on_dark()
    {
        var yellow = Fill(64, 255, 240, 60);
        var onLight = AlbumTint.Pick(yellow, lightBackground: true)!.Value;
        Assert.True(Math.Max(onLight.R, Math.Max(onLight.G, onLight.B)) <= 184);   // brightness ≤ 0.72

        var navy = Fill(64, 20, 30, 90);
        var onDark = AlbumTint.Pick(navy, lightBackground: false)!.Value;
        Assert.True(Math.Max(onDark.R, Math.Max(onDark.G, onDark.B)) >= 183);     // brightness ≥ 0.72
    }
}
