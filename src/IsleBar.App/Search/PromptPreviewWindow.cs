using IsleBar.App.Interop;
using IsleBar.App.Options;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace IsleBar.App.Search;

/// <summary>
/// Long prompt preview: shows the full text that overflows the input box, wrapped, above the search box (doesn't take focus — typing continues in the original box).
/// At first, overflowing moved to the compose window, but characters typed at that moment were lost (09-30 PC: "settings" → "sets") —
/// so on overflow we only show it, and open the compose window (multi-line) only when the person pauses, like Shift+Enter or a multi-line paste.
/// A blinking caret sits at the real insertion point, so — even though the typing happens in the hidden input box — you can see where you are (user 09-30).
/// </summary>
internal sealed class PromptPreviewWindow : Window
{
    private const int WidthDip = 460;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private readonly TextBlock _text;
    private readonly StackPanel _panel;
    private readonly ScrollViewer _scroll;
    private const double HintDip = 32;   // the "Shift+Enter" line under the text, plus the panel padding
    private readonly Run _before = new();
    // The caret is a thin bar (|). A zero-width space just before it carries strong negative tracking, which pulls the bar left
    // so it hugs the character like a real text cursor instead of floating in a wide gap (the box-drawing "│" sat in a fat cell — user 09-30).
    private readonly Run _kern = new() { Text = "​", CharacterSpacing = -520 };
    private readonly Run _caret = new() { Text = "|", CharacterSpacing = -260 };
    private readonly Run _after = new();
    private readonly Brush _caretOn;
    private readonly Brush _caretOff = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _blink;

    public PromptPreviewWindow()
    {
        SystemBackdrop = new Microsoft.UI.Xaml.Media.DesktopAcrylicBackdrop();
        PopupPlacement.MakeToolPopup(this);
        // Drop the border AND the title bar (MakeToolPopup leaves a bordered frame with the "WinUI Desktop" caption) — a clean,
        // chromeless acrylic card like the notification deck, which is the look the user wants back (09-30).
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
        }

        _caretOn = Application.Current.Resources.TryGetValue("TextControlForeground", out var fg) && fg is Brush b
            ? b
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);   // adapts with the theme when present; a neutral grey reads on the acrylic either way
        _caret.Foreground = _caretOn;

        // No line cap: the whole prompt shows, growing up to the screen top; past that it scrolls, following the caret
        // (it used to stop at 10 lines with "…", so a long prompt couldn't be seen at a glance — user 10-01).
        _text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14, CharacterSpacing = -11 };
        _text.Inlines.Add(_before);
        _text.Inlines.Add(_kern);
        _text.Inlines.Add(_caret);
        _text.Inlines.Add(_after);

        _panel = new StackPanel { Padding = new Thickness(12, 10, 12, 10), Width = WidthDip };
        _scroll = new ScrollViewer { Content = _text, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        _panel.Children.Add(_scroll);
        _panel.Children.Add(new TextBlock { Text = "Shift+Enter ⏎", FontSize = 11, Opacity = 0.5, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 6, 0, 0) });
        Content = _panel;
        // To edit a long question, a click opens the compose box (10-03). On release, and only once the click is over: opened on the
        // press, the box was activated while the button was still down on this window and closed again at once (seen on PC 10-03).
        // handledEventsToo: the scroll viewer marks pointer events handled; a transparent background makes the whole card clickable.
        _panel.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _panel.AddHandler(UIElement.PointerReleasedEvent, new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => Clicked?.Invoke())), handledEventsToo: true);

        // Blink by swapping the caret's colour (not its text), so the surrounding text never reflows.
        _blink = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread().CreateTimer();   // the Window's own DispatcherQueue property is not set yet inside the ctor
        _blink.Interval = TimeSpan.FromMilliseconds(530);   // the Windows caret cadence
        _blink.Tick += (_, _) => _caret.Foreground = ReferenceEquals(_caret.Foreground, _caretOn) ? _caretOff : _caretOn;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ex = NativeMethods.Unsigned32(NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE));
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, NativeMethods.Signed32(ex | WS_EX_NOACTIVATE));
        AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
    }

    /// <param name="caret">Insertion point in <paramref name="text"/> (the input box's caret index).</param>
    public void Show(string text, int caret, int anchorX, int bandTop)
    {
        var at = Math.Clamp(caret, 0, text.Length);
        _before.Text = text[..at];
        _after.Text = text[at..];
        _caret.Foreground = _caretOn;   // solid the instant a key lands, then resume blinking (like a real caret)
        _blink.Start();

        _scroll.MaxHeight = PopupPlacement.MaxHeightAbove(anchorX, bandTop) - HintDip;
        PopupPlacement.SizeToContent(this, _panel, WidthDip);
        PopupPlacement.PlaceAbove(this, anchorX, bandTop);
        AppWindow.Show(activateWindow: false);
        FollowCaret(at, text.Length);
    }

    /// <summary>When the prompt is taller than the screen, keep the part around the caret in view (typing usually happens at the end).</summary>
    private void FollowCaret(int caret, int length)
    {
        _scroll.UpdateLayout();
        var hidden = _scroll.ExtentHeight - _scroll.ViewportHeight;
        if (hidden <= 0)
        {
            return;
        }

        var ratio = length == 0 ? 1.0 : (double)caret / length;
        var target = Math.Clamp((ratio * _scroll.ExtentHeight) - (_scroll.ViewportHeight / 2), 0, hidden);
        _scroll.ChangeView(null, target, null, disableAnimation: true);
    }

    /// <summary>The preview was clicked — the bar opens the multi-line compose box with the question.</summary>
    public event Action? Clicked;

    public bool UnderCursor() => PopupPlacement.UnderCursor(this);

    public void Hide()
    {
        _blink.Stop();
        AppWindow.Hide();
    }
}
