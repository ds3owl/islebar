using IsleBar.App.Interop;
using IsleBar.App.Options;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using static IsleBar.App.Interop.NativeMethods;

namespace IsleBar.App.IslandUi;

/// <summary>
/// One of the cards peeking out behind the front card in the message deck. It is its <b>own</b> borderless, non-activating,
/// topmost window with the system's rounded corner (anti-aliased) — so the deck's corners are smooth AND the desktop shows
/// between the cards, with no white fringe. A single clipped window couldn't do both: the region clip isn't anti-aliased
/// (stair-stepped corners) and leaked the window background as a white hairline (user 09-30: solve both).
/// </summary>
internal sealed class PeekWindow : Window
{
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private readonly Grid _fill;

    public PeekWindow()
    {
        PopupPlacement.MakeToolPopup(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
        }

        _fill = new Grid();
        Content = _fill;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ex = Unsigned32(GetWindowLong(hwnd, GWL_EXSTYLE));
        SetWindowLong(hwnd, GWL_EXSTYLE, Signed32(ex | WS_EX_NOACTIVATE));
        AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
    }

    /// <summary>Grey shade for depth level (1 = just behind the front, 2 = further back).</summary>
    public void SetColor(bool dark, int level)
        => _fill.Background = new SolidColorBrush(dark
            ? Color.FromArgb(255, (byte)(60 - (level * 10)), (byte)(60 - (level * 10)), (byte)(64 - (level * 10)))
            : Color.FromArgb(255, (byte)(232 - (level * 12)), (byte)(232 - (level * 12)), (byte)(236 - (level * 12))));

    /// <summary>Place the peek at <paramref name="rect"/> (screen pixels), rounded and outlined by the system, and show it without focus.</summary>
    public void Place(Windows.Graphics.RectInt32 rect, int radius, bool dark)
    {
        AppWindow.MoveAndResize(rect);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var border = dark ? 0x00505055u : 0x00D8D8DEu;
        DwmSetWindowAttributeUInt(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(uint));
        _ = radius;   // the system chooses the corner radius; kept for signature symmetry with the front card
        AppWindow.Show(activateWindow: false);
    }

    public void HidePeek() => AppWindow.Hide();

    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);
}
