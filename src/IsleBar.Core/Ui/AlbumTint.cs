namespace IsleBar.Core.Ui;

/// <summary>
/// Picks one representative color from the album art — the music bars and playback bar are painted with it (design G2, 09-30: like a waveform
/// follows the album color). A plain average looks muddy, so <b>saturated pixels are weighted more</b> (Boring Notch uses a plain average + brightness correction).
/// To stay visible on the pill background it is kept not too bright on light backgrounds and not too dark on dark ones.
/// </summary>
public static class AlbumTint
{
    /// <summary>
    /// <paramref name="bgra"/>: pixels of the downscaled image (4 bytes each, B,G,R,A order). Null if nearly achromatic (the usual color is used).
    /// </summary>
    public static (byte R, byte G, byte B)? Pick(ReadOnlySpan<byte> bgra, bool lightBackground)
    {
        double r = 0, g = 0, b = 0, total = 0;
        for (var i = 0; i + 3 < bgra.Length; i += 4)
        {
            var (pr, pg, pb) = (bgra[i + 2] / 255.0, bgra[i + 1] / 255.0, bgra[i] / 255.0);
            var (_, s, v) = ToHsv(pr, pg, pb);
            var weight = (s * v * s) + 0.002;   // more vivid colors weigh more (saturation counted twice — so the main color wins even with white/black/gray backgrounds mixed in)
            r += pr * weight;
            g += pg * weight;
            b += pb * weight;
            total += weight;
        }

        if (total <= 0)
        {
            return null;
        }

        var (h, sat, val) = ToHsv(r / total, g / total, b / total);
        if (sat < 0.12)
        {
            return null;   // black-and-white album — do not force a color onto it
        }

        sat = Math.Max(sat, 0.45);
        val = lightBackground ? Math.Clamp(val, 0.45, 0.72) : Math.Clamp(val, 0.72, 0.95);
        var (fr, fg, fb) = FromHsv(h, sat, val);
        return ((byte)Math.Round(fr * 255), (byte)Math.Round(fg * 255), (byte)Math.Round(fb * 255));
    }

    private static (double H, double S, double V) ToHsv(double r, double g, double b)
    {
        var max = Math.Max(r, Math.Max(g, b));
        var min = Math.Min(r, Math.Min(g, b));
        var d = max - min;
        double h = 0;
        if (d > 0)
        {
            h = max == r ? ((g - b) / d % 6) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
            h *= 60;
            if (h < 0)
            {
                h += 360;
            }
        }

        return (h, max <= 0 ? 0 : d / max, max);
    }

    private static (double R, double G, double B) FromHsv(double h, double s, double v)
    {
        var c = v * s;
        var x = c * (1 - Math.Abs((h / 60 % 2) - 1));
        var m = v - c;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return (r + m, g + m, b + m);
    }
}
