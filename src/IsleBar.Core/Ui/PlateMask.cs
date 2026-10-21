namespace IsleBar.Core.Ui;

/// <summary>
/// Turns an app logo image (BGRA pixels) into an alpha mask for a single-color icon — for the Codex mark taken from
/// OpenAI's ChatGPT app, whose tile logos come either on a transparent background or on a plain plate.
/// Transparent: the opaque pixels are the mark. Plated: the pixels that differ from the plate color (read from the border) are.
/// The original shape is used as-is; only the edges are smoothed and the result is downscaled by area averaging.
/// </summary>
public static class PlateMask
{
    /// <summary>
    /// Builds a <paramref name="size"/>×<paramref name="size"/> alpha mask from <paramref name="bgra"/>
    /// (<paramref name="width"/> wide, <paramref name="height"/> tall). Null when no mark can be told apart.
    /// </summary>
    public static byte[]? Build(byte[] bgra, int width, int height, int size)
    {
        if (size <= 0 || width <= 0 || height <= 0 || bgra.Length < width * height * 4)
        {
            return null;
        }

        var (on, plated) = Mark(bgra, width, height);
        int left = width, top = height, right = -1, bottom = -1;
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                if (on[(y * width) + x] != 0)
                {
                    left = Math.Min(left, x);
                    top = Math.Min(top, y);
                    right = Math.Max(right, x);
                    bottom = Math.Max(bottom, y);
                }
            }
        }

        // nothing, or (on a plate) everything — a plate with no mark on it; a bare mark may fill its whole image
        if (right < 0 || (plated && (right - left + 1) * (bottom - top + 1) > width * height * 95 / 100))
        {
            return null;
        }

        // crop to the mark and center it in a square with a small margin
        int cw = right - left + 1, ch = bottom - top + 1;
        var side = Math.Max(cw, ch) + (Math.Max(cw, ch) / 10);
        var square = new byte[side * side];
        int ox = (side - cw) / 2, oy = (side - ch) / 2;
        for (var y = 0; y < ch; y++)
        {
            Array.Copy(on, ((top + y) * width) + left, square, ((oy + y) * side) + ox, cw);
        }

        square = GlyphMask.Threshold(GlyphMask.BoxBlur(square, side, Math.Max(1, side / 128)), 127);
        return GlyphMask.Downscale(square, side, size);
    }

    /// <summary>255 where the mark is, 0 elsewhere; and whether the picture had a plate.</summary>
    private static (byte[] On, bool Plated) Mark(byte[] bgra, int width, int height)
    {
        var count = width * height;
        var clear = 0;
        for (var i = 0; i < count; i++)
        {
            if (bgra[(i * 4) + 3] < 16)
            {
                clear++;
            }
        }

        var on = new byte[count];
        if (clear * 20 >= count)
        {
            // transparent background (at least 5% clear): the opaque pixels are the mark
            for (var i = 0; i < count; i++)
            {
                on[i] = bgra[(i * 4) + 3] > 127 ? (byte)255 : (byte)0;
            }

            return (on, false);
        }

        // plated: the plate color is the median of the border pixels
        var border = new List<int>();
        for (var x = 0; x < width; x++)
        {
            border.Add(x);
            border.Add(((height - 1) * width) + x);
        }

        for (var y = 0; y < height; y++)
        {
            border.Add(y * width);
            border.Add((y * width) + width - 1);
        }

        var plate = new int[3];
        for (var c = 0; c < 3; c++)
        {
            var values = border.Select(i => (int)bgra[(i * 4) + c]).OrderBy(v => v).ToList();
            plate[c] = values[values.Count / 2];
        }

        for (var i = 0; i < count; i++)
        {
            var p = i * 4;
            var diff = Math.Max(Math.Abs(bgra[p] - plate[0]), Math.Max(Math.Abs(bgra[p + 1] - plate[1]), Math.Abs(bgra[p + 2] - plate[2])));
            on[i] = bgra[p + 3] > 127 && diff > 60 ? (byte)255 : (byte)0;
        }

        return (on, true);
    }
}
