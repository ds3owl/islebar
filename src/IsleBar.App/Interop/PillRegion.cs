using IsleBar.Core.Ui;
using static IsleBar.App.Interop.NativeMethods;

namespace IsleBar.App.Interop;

/// <summary>
/// Clips the window shape to a pill (rectangle with rounded ends).
///
/// Why: our window <b>covers</b> the real Windows search box. Clipping the corners to a pill
/// lets the real search box's 1px border show through outside it, so the edge looks natural.
/// The inside is filled with the real search box's background color (no background snapshot, so
/// stale images don't get baked in when icons move).
/// </summary>
internal static class PillRegion
{
    /// <summary>Cover only this far inside so the real search box border stays visible.</summary>
    public static int EdgeInset => PillGeometry.EdgeInset(DpiSetup.Scale);

    /// <summary>
    /// Clips the window to a pill. Region ownership passes to the window, so <b>do not DeleteObject</b>
    /// (deleting it breaks the next paint).
    /// </summary>
    public static void Apply(IntPtr window, int width, int height, bool flush = false)
    {
        // Coordinate math lives in Core (PillGeometry) — moved there to test without a window (09-29).
        if (PillGeometry.TryCompute(width, height, DpiSetup.Scale, flush) is not { } r)
        {
            return;   // too small → clipping would make the window disappear
        }

        var region = CreateRoundRectRgn(r.Left, r.Top, r.Right, r.Bottom, r.Diameter, r.Diameter);
        if (region == IntPtr.Zero)
        {
            return;
        }

        if (SetWindowRgn(window, region, redraw: true) == 0)
        {
            DeleteObject(region);   // on failure ownership was not transferred, so we delete it
        }
    }

    /// <summary>Removes the clipping (when detaching from the taskbar).</summary>
    public static void Clear(IntPtr window) => SetWindowRgn(window, IntPtr.Zero, redraw: true);

    /// <summary>Turns off the system's rounded corners, border and shadow (we draw the shape ourselves).</summary>
    public static void DisableSystemFrame(IntPtr window)
    {
        var flat = DWMWCP_DONOTROUND;
        DwmSetWindowAttribute(window, DWMWA_WINDOW_CORNER_PREFERENCE, ref flat, sizeof(int));
        var none = DWMWA_COLOR_NONE;
        DwmSetWindowAttributeUInt(window, DWMWA_BORDER_COLOR, ref none, sizeof(uint));
    }
}
