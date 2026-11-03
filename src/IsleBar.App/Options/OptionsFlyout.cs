using IsleBar.App.Interop;
using IsleBar.Core.Configuration;
using IsleBar.Core.Localization;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IsleBar.App.Options;

/// <summary>
/// Quick launch options that pop up right above the search box when ⌄ is pressed (only those pinned in settings) + gear (open settings).
/// Closes when clicking elsewhere or pressing Esc. Same look and position as the Python version's <c>open_fly</c>.
/// </summary>
internal sealed class OptionsFlyout : Window
{
    private const int WidthDip = 300;

    private readonly IsleBarSettings _settings;
    private readonly Action _changed;
    private readonly Action _openSettings;
    private bool _closing;

    public OptionsFlyout(IsleBarSettings settings, LanguageStrings text, Action changed, Action openSettings)
    {
        _settings = settings;
        _changed = changed;
        _openSettings = openSettings;
        Title = text.Options;
        SystemBackdrop = new DesktopAcrylicBackdrop();

        var panel = new StackPanel { Padding = new Thickness(16, 14, 16, 16), Spacing = 10 };

        // Same compact toggle as the settings window: trim the 10 dip empty rows the stock ToggleSwitch keeps above/below its
        // knob, so a pinned "remote control" switch lines up with the segmented-button rows instead of standing taller (10-01).
        panel.Resources["ToggleSwitchPreContentMargin"] = 4.0;
        panel.Resources["ToggleSwitchPostContentMargin"] = 4.0;

        // Header: "Launch options" + gear
        var head = new Grid();
        head.Children.Add(new TextBlock
        {
            Text = text.Options,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var gear = new Button
        {
            Content = new FontIcon { Glyph = "", FontSize = 14 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(6),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        ToolTipService.SetToolTip(gear, text.Settings);
        gear.Click += (_, _) =>
        {
            CloseQuietly();
            _openSettings();
        };
        head.Children.Add(gear);
        panel.Children.Add(head);

        var shown = settings.Quick.Where(k => OptionControls.IsShown(settings, k)).ToList();
        foreach (var key in shown)
        {
            panel.Children.Add(OptionControls.Row(settings, text, key, compact: true, changed));
        }

        Content = panel;
        panel.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Escape)
            {
                CloseQuietly();
            }
        };

        // Closes when clicking elsewhere
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                CloseQuietly();
            }
        };
        Closed += (_, _) => _closing = true;

        PopupPlacement.MakeToolPopup(this);
    }

    /// <summary>Opens above the search box, centred on <paramref name="anchorX"/>, 12 above the taskbar's top edge (<paramref name="bandTop"/>).</summary>
    public void ShowAbove(int anchorX, int bandTop)
        => PopupPlacement.ShowSized(this, (FrameworkElement)Content, WidthDip, anchorX, bandTop);

    private void CloseQuietly()
    {
        if (_closing)
        {
            return;
        }

        _closing = true;
        Close();
    }
}

/// <summary>Common handling for small windows popped up above the search box.</summary>
internal static class PopupPlacement
{
    /// <summary>
    /// Whether the mouse is over this popup right now. A click on a popup takes activation from the bar even with NOACTIVATE,
    /// and the bar ended typing on that — closing the popup before the click arrived (file results did nothing, a click on the
    /// long-question preview wiped the question; 10-03). The bar checks this before ending typing.
    /// </summary>
    public static bool UnderCursor(Window window)
    {
        if (!window.AppWindow.IsVisible || !NativeMethods.GetCursorPos(out var p)
            || !NativeMethods.GetWindowRect(WinRT.Interop.WindowNative.GetWindowHandle(window), out var r))
        {
            return false;
        }

        return p.X >= r.Left && p.X < r.Right && p.Y >= r.Top && p.Y < r.Bottom;
    }

    /// <summary>No title bar, fixed size, topmost, hidden from taskbar buttons/Alt+Tab. Rounded corners and shadow are the system's.</summary>
    public static void MakeToolPopup(Window window)
    {
        if (window.AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
            presenter.IsAlwaysOnTop = true;
        }

        window.AppWindow.IsShownInSwitchers = false;
    }

    /// <summary>
    /// Shows it off-screen first so the controls' templates are fully built before measuring, then moves it above the search box.
    /// (Measuring before showing gives a small width because button templates don't exist yet, and buttons overlap — measured on PC 09-29)
    /// </summary>
    /// <param name="placed">Runs once the window has been put in place (under a picture of the screen until it is drawn).</param>
    public static void ShowSized(Window window, FrameworkElement content, int widthDip, int anchorX, int bandTop, Action? placed = null)
    {
        window.AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            content.Loaded -= OnLoaded;
            SizeAndPlaceAbove(window, content, widthDip, anchorX, bandTop);
            placed?.Invoke();
        }

        content.Loaded += OnLoaded;
        window.Activate();
    }

    /// <summary>
    /// Fits to the content height and places above the search box in one window operation. While the popup is on screen, a separate
    /// resize and move each reached the screen as their own frame — the long-question preview flickered as you typed: a tall empty
    /// frame, or a white strip at the bottom when a line wrapped (promo film 9.35 s / 9.40 s, user 11-03). Does nothing when the
    /// size and place haven't changed, which is most keystrokes.
    /// </summary>
    public static void SizeAndPlaceAbove(Window window, FrameworkElement content, int widthDip, int anchorX, int bandTop) =>
        _ = SizeAndPlace(window, content, widthDip, anchorX, bandTop);

    /// <summary><see cref="SizeAndPlaceAbove"/>, telling whether the window actually changed.</summary>
    private static bool SizeAndPlace(Window window, FrameworkElement content, int widthDip, int anchorX, int bandTop)
    {
        var app = window.AppWindow;
        var size = ContentSize(window, content, widthDip);
        var at = AbovePosition(size, anchorX, bandTop);
        if (app.Size.Width == size.Width && app.Size.Height == size.Height && app.Position.X == at.X && app.Position.Y == at.Y)
        {
            return false;
        }

        if (app.IsVisible && !IsWaitingForFrame(window))
        {
            // On screen, the window reaches its new size a few frames before XAML has drawn it there (a band under the old text
            // when a line wrapped; an empty outline when placed on screen for the first time) — hold a picture of the screen as
            // it is now over both places until it has been drawn. A cloaked window isn't seen, so needs none.
            int left = at.X, top = at.Y, right = at.X + size.Width, bottom = at.Y + size.Height;
            if (app.Position.X > -30000)
            {
                left = Math.Min(left, app.Position.X);
                top = Math.Min(top, app.Position.Y);
                right = Math.Max(right, app.Position.X + app.Size.Width);
                bottom = Math.Max(bottom, app.Position.Y + app.Size.Height);
            }

            var shadow = DpiSetup.Px(16);   // the system shadow around the window moves with it; the bottom edge stays put above the bar
            ScreenGhost.Popup.Cover(left - shadow, top - shadow, right + shadow, bottom);
        }

        app.MoveAndResize(new Windows.Graphics.RectInt32(at.X, at.Y, size.Width, size.Height));
        return true;
    }

    /// <summary>A popup shown cloaked, waiting for its first drawn frame: its handle and how many frames have gone by.</summary>
    private sealed class Waiting(IntPtr hwnd)
    {
        public IntPtr Hwnd { get; } = hwnd;
        public int Frames { get; set; }
    }

    private static readonly Dictionary<Window, Waiting> s_waitingForFrame = new();

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private const int DWMWA_CLOAK = 13;

    /// <summary>Cloaked, a window is shown and drawn as usual but the system doesn't put it on screen. Takes the handle, not the
    /// Window: the Window may be closed by the time it is uncloaked, and touching a closed Window throws.</summary>
    private static void Cloak(IntPtr hwnd, bool on)
    {
        var value = on ? 1 : 0;
        _ = DwmSetWindowAttribute(hwnd, DWMWA_CLOAK, ref value, sizeof(int));   // a destroyed handle just fails — nothing to do
    }

    /// <summary>
    /// Shows a no-activate popup above the search box. Already on screen, it is just resized in place. Otherwise it is shown in
    /// place but cloaked, and uncloaked once XAML has drawn it: shown straight away, the first frame was the window's empty outline
    /// or a blank box before its content appeared (the long-question preview on paste, user 11-03). Off screen didn't help —
    /// XAML only drew the content once the window was on a screen.
    /// </summary>
    public static void ShowDrawnAbove(Window window, FrameworkElement content, int widthDip, int anchorX, int bandTop)
    {
        var app = window.AppWindow;
        if (s_waitingForFrame.TryGetValue(window, out var waiting))
        {
            // Still cloaked from a moment ago. Hidden meanwhile (the preview hides and shows as the text crosses the edge) → show
            // it again; resized → wait for a frame drawn at the new size before it is uncloaked.
            if (SizeAndPlace(window, content, widthDip, anchorX, bandTop))
            {
                waiting.Frames = 0;
            }

            if (!app.IsVisible)
            {
                app.Show(activateWindow: false);
            }

            return;
        }

        if (app.IsVisible && app.Position.X > -30000)
        {
            SizeAndPlaceAbove(window, content, widthDip, anchorX, bandTop);
            return;
        }

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(window);
        var entry = new Waiting(hwnd);
        s_waitingForFrame[window] = entry;
        Cloak(hwnd, true);
        var size = ContentSize(window, content, widthDip);   // hidden, so the tall temporary layout isn't seen
        var at = AbovePosition(size, anchorX, bandTop);
        app.MoveAndResize(new Windows.Graphics.RectInt32(at.X, at.Y, size.Width, size.Height));
        app.Show(activateWindow: false);

        void Done()
        {
            CompositionTarget.Rendering -= OnRendering;
            window.Closed -= OnClosed;
            s_waitingForFrame.Remove(window);
            Cloak(hwnd, false);   // also when hidden meanwhile, so the next show doesn't start cloaked
        }

        void OnRendering(object? sender, object e)
        {
            if (++entry.Frames >= 3)
            {
                Done();   // a Rendering tick comes before its frame is drawn; give the first frame time to reach the screen
            }
        }

        void OnClosed(object sender, WindowEventArgs e) => Done();   // closed while waiting (results closed by Enter/Esc at once)

        window.Closed += OnClosed;
        CompositionTarget.Rendering += OnRendering;
    }

    /// <summary>Whether <paramref name="window"/> was just shown cloaked by <see cref="ShowDrawnAbove"/> and is still waiting for its first frame.</summary>
    public static bool IsWaitingForFrame(Window window) => s_waitingForFrame.ContainsKey(window);

    /// <summary>Cloaks or uncloaks a window: shown and drawn as usual, but kept off the screen while cloaked.</summary>
    public static void SetCloaked(Window window, bool on) => Cloak(WinRT.Interop.WindowNative.GetWindowHandle(window), on);

    /// <summary>Where and how big the window would be above the search box for this content, without changing the window
    /// (except a hidden window's temporary layout size).</summary>
    public static Windows.Graphics.RectInt32 FitAbove(Window window, FrameworkElement content, int widthDip, int anchorX, int bandTop)
    {
        var size = ContentSize(window, content, widthDip);
        var at = AbovePosition(size, anchorX, bandTop);
        return new Windows.Graphics.RectInt32(at.X, at.Y, size.Width, size.Height);
    }

    /// <summary>The outer window size that fits the content (width is the larger of <paramref name="widthDip"/> and the content).</summary>
    private static Windows.Graphics.SizeInt32 ContentSize(Window window, FrameworkElement content, int widthDip)
    {
        var app = window.AppWindow;
        // Width: the larger of the unconstrained content width (the minimum where buttons don't overlap) and the default width
        content.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        var width = Math.Ceiling(Math.Max(widthDip, content.DesiredSize.Width));
        content.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var wanted = content.DesiredSize.Height;

        // Off screen (hidden, or parked at -32000 before the first show) the window may be laid out against a tall client area first,
        // so the arranged height below isn't clipped (a fixed 600px here clipped long prompts at ~13 lines; sizing to the whole screen
        // made the window flash full-height on every keystroke — user 10-01). On screen that temporary size would itself be seen,
        // so there the measured height alone decides.
        var onScreen = app.IsVisible && app.Position.X > -30000;
        if (!onScreen)
        {
            var workArea = DisplayArea.GetFromWindowId(app.Id, DisplayAreaFallback.Primary).WorkArea;
            var layoutHeight = Math.Min(workArea.Height, Math.Max(DpiSetup.Px(600), DpiSetup.Px((int)Math.Ceiling(wanted)) + DpiSetup.Px(16)));
            app.ResizeClient(new Windows.Graphics.SizeInt32(DpiSetup.Px((int)width), layoutHeight));
        }

        // Height: the height actually arranged at that width (anchored at top, so leftover space isn't included)
        content.VerticalAlignment = VerticalAlignment.Top;
        content.UpdateLayout();
        // Size by the outer size: on a window without a title bar, ResizeClient grows by the height of the nonexistent title bar (measured on PC 09-29)
        var client = app.ClientSize;
        var outer = app.Size;
        var chromeW = outer.Width - client.Width;   // sum of left and right borders
        var hasTitle = app.Presenter is OverlappedPresenter { HasTitleBar: true };
        var chromeH = hasTitle ? outer.Height - client.Height : chromeW;   // includes the title bar height if there is one
        // ActualHeight can still be clipped to the window's previous size (the resize lands asynchronously) — the long-prompt
        // compose window stayed ~100 tall under 40 lines (user 10-01) — so never go below the measured height. An arrangement at
        // another width says nothing about this one, so it only counts when the window already has this width.
        var arranged = client.Width == DpiSetup.Px((int)width) ? content.ActualHeight : 0;
        var height = DpiSetup.Px((int)Math.Ceiling(Math.Max(arranged, wanted))) + chromeH;
        var work = DisplayArea.GetFromWindowId(app.Id, DisplayAreaFallback.Primary).WorkArea;
        height = Math.Min(height, work.Height - DpiSetup.Px(24));   // keep within the work area (above the taskbar) — if it overflows, scroll inside the window
        return new Windows.Graphics.SizeInt32(DpiSetup.Px((int)width) + chromeW, height);
    }

    /// <summary>
    /// Tallest a popup above the search box may be, in DIP: from the top of the screen down to the taskbar, less a margin — long
    /// prompts grow up to this instead of stopping at a fixed number of lines (user 10-01: a long prompt was cut off).
    /// </summary>
    public static double MaxHeightAbove(int anchorX, int bandTop)
    {
        var area = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(anchorX, bandTop - 1), DisplayAreaFallback.Primary);
        var px = bandTop - area.OuterBounds.Y - DpiSetup.Px(12) - DpiSetup.Px(24);
        return Math.Max(120, px / DpiSetup.Scale);
    }

    /// <summary>Where a window of <paramref name="size"/> goes above the search box: centred on <paramref name="anchorX"/> (the pill's centre), without going off-screen.</summary>
    private static Windows.Graphics.PointInt32 AbovePosition(Windows.Graphics.SizeInt32 size, int anchorX, int bandTop)
    {
        var area = DisplayArea.GetFromPoint(new Windows.Graphics.PointInt32(anchorX, bandTop - 1), DisplayAreaFallback.Primary);
        var right = area.OuterBounds.X + area.OuterBounds.Width;
        var x = Math.Max(area.OuterBounds.X + DpiSetup.Px(8), Math.Min(anchorX - (size.Width / 2), right - size.Width - DpiSetup.Px(8)));
        var y = Math.Max(area.OuterBounds.Y, bandTop - size.Height - DpiSetup.Px(12));
        return new Windows.Graphics.PointInt32(x, y);
    }
}
