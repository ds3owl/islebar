namespace IsleBar.Core.Ui;

/// <summary>Rectangle to clip into a pill. All values are screen pixels.</summary>
public readonly record struct PillRect(int Left, int Top, int Right, int Bottom, int Diameter);

/// <summary>
/// Coordinate math for clipping the search bar into a pill shape.
///
/// <para><b>Why this lives in Core.</b> Being off by even 1 pixel hides the real search box border or
/// squashes the pill, and inside window code you only find out by launching it on a PC.
/// With only the DPI scale injected it is pure math, so it can be verified here.</para>
/// </summary>
public static class PillGeometry
{
    /// <summary>Always inset by at least this much.</summary>
    public const int MinInset = 2;

    /// <summary>Margin at scale 1.</summary>
    public const double BaseInset = 1.5;

    /// <summary>Pixels to inset so the real search box border stays visible.</summary>
    public static int EdgeInset(double scale) => Math.Max(MinInset, (int)Math.Round(BaseInset * scale));

    /// <summary>
    /// The clipping rectangle. Null if too small to form the shape —
    /// <b>in that case do not clip</b> (creating a region with 0 or negative size makes the window disappear).
    /// </summary>
    /// <param name="flush">
    /// If true, use the window size as-is with no margin (covering the real search box border too) — used when the notice border is on.
    /// With a margin, a line of the real search box's bright border showed outside the notice border, so it did not look flush (user feedback 09-30).
    /// </param>
    public static PillRect? TryCompute(int width, int height, double scale, bool flush = false)
    {
        var inset = flush ? 0 : EdgeInset(scale);
        var diameter = height - (2 * inset);
        if (diameter <= 0 || width <= 2 * inset)
        {
            return null;
        }

        // right/bottom are exclusive, hence +1 (CreateRoundRectRgn rule)
        return new PillRect(inset, inset, width - inset + 1, height - inset + 1, diameter);
    }
}
