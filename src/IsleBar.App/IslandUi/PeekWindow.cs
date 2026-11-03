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
/// <para>
/// It comes onto the screen cloaked and is only uncloaked once it has been drawn AND the deck asked for it (<see cref="Reveal"/>):
/// shown straight away, each peek was a grey strip floating above the bar before the front card had grown under it, and its
/// first frame could be blank (review 11-03).
/// </para>
/// </summary>
internal sealed class PeekWindow : Window
{
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    // Rendering ticks to wait after showing before the peek counts as drawn: a tick comes before its frame (same as PopupPlacement.ShowDrawnAbove)
    private const int FramesBeforeDrawn = 3;
    private readonly Grid _fill;
    private bool _counting;       // waiting for the first frames after being shown
    private int _frames;
    private bool _revealWanted;   // the deck asked for it to be seen; it is uncloaked as soon as it has been drawn

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
        Closed += (_, _) => StopCounting();   // a closed window must not stay hooked to the frame clock
    }

    /// <summary>Grey shade for depth level (1 = just behind the front, 2 = further back).</summary>
    public void SetColor(bool dark, int level)
        => _fill.Background = new SolidColorBrush(dark
            ? Color.FromArgb(255, (byte)(60 - (level * 10)), (byte)(60 - (level * 10)), (byte)(64 - (level * 10)))
            : Color.FromArgb(255, (byte)(232 - (level * 12)), (byte)(232 - (level * 12)), (byte)(236 - (level * 12))));

    /// <summary>
    /// Place the peek at <paramref name="rect"/> (screen pixels), rounded and outlined by the system. Already on screen it just
    /// follows the front card; otherwise it is shown without focus but cloaked, and stays unseen until <see cref="Reveal"/>.
    /// </summary>
    public void Place(Windows.Graphics.RectInt32 rect, int radius, bool dark)
    {
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var border = dark ? 0x00505055u : 0x00D8D8DEu;
        DwmSetWindowAttributeUInt(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(uint));
        _ = radius;   // the system chooses the corner radius; kept for signature symmetry with the front card

        if (AppWindow.IsVisible)
        {
            AppWindow.MoveAndResize(rect);
            return;
        }

        // Cloak before it reaches the screen: XAML only draws once the window is shown, so its first frame would be blank.
        PopupPlacement.SetCloaked(this, true);
        _revealWanted = false;
        AppWindow.MoveAndResize(rect);
        AppWindow.Show(activateWindow: false);
        _frames = 0;
        if (!_counting)
        {
            _counting = true;
            CompositionTarget.Rendering += OnRendering;
        }
    }

    /// <summary>Let the peek be seen: uncloaked now if it has been drawn, otherwise as soon as it has.</summary>
    public void Reveal()
    {
        _revealWanted = true;
        if (!_counting && AppWindow.IsVisible)
        {
            PopupPlacement.SetCloaked(this, false);
        }
    }

    /// <summary>Keep the peek on screen but unseen until the next <see cref="Reveal"/> (the front card is about to grow again).</summary>
    public void Conceal()
    {
        _revealWanted = false;
        if (AppWindow.IsVisible)
        {
            PopupPlacement.SetCloaked(this, true);
        }
    }

    /// <summary>Hide the peek. Cloaked first, so it goes without a last blank frame and the next show starts unseen.</summary>
    public void HidePeek()
    {
        StopCounting();
        _revealWanted = false;
        PopupPlacement.SetCloaked(this, true);
        AppWindow.Hide();
    }

    public IntPtr Handle => WinRT.Interop.WindowNative.GetWindowHandle(this);

    private void OnRendering(object? sender, object e)
    {
        if (++_frames < FramesBeforeDrawn)
        {
            return;
        }

        StopCounting();
        if (_revealWanted && AppWindow.IsVisible)
        {
            PopupPlacement.SetCloaked(this, false);
        }
    }

    private void StopCounting()
    {
        if (_counting)
        {
            _counting = false;
            CompositionTarget.Rendering -= OnRendering;
        }
    }
}
