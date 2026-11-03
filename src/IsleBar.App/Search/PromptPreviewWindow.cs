using IsleBar.App.Interop;
using IsleBar.App.IslandUi;
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
/// <para>
/// Two cards take turns. A window resized on screen reaches the screen at its new size a few frames before XAML has drawn
/// it at that size, so each time the text wrapped onto a new line (or back) the card showed the old text with a see-through
/// or white band under it for 1–3 frames (60 fps capture, user 11-03: "줄바뀌면 깜빡이는거 여전한거 같은디"). Now a change of
/// size is drawn on the other card while it is cloaked; once drawn, it is uncloaked over the old one, which is then hidden.
/// The card on screen is never resized.
/// </para>
/// </summary>
internal sealed class PromptPreviewWindow
{
    private PreviewCard _front = new();
    private PreviewCard _back = new();
    private PreviewCard? _swapping;   // the back card, cloaked, being drawn at the new size
    private int _frames;

    public PromptPreviewWindow()
    {
        _front.Clicked += () => Clicked?.Invoke();
        _back.Clicked += () => Clicked?.Invoke();
    }

    /// <summary>The preview was clicked — the bar opens the multi-line compose box with the question.</summary>
    public event Action? Clicked;

    public void ApplyFonts(string? language)
    {
        AppFonts.Apply(_front, language);
        AppFonts.Apply(_back, language);
    }

    /// <param name="caret">Insertion point in <paramref name="text"/> (the input box's caret index).</param>
    public void Show(string text, int caret, int anchorX, int bandTop)
    {
        if (_swapping is { } next)
        {
            // Still being drawn at the size it will appear with: follow the typing there; a further change of size restarts the wait.
            next.SetText(text, caret);
            if (next.Fit(anchorX, bandTop) is var r && !SameRect(r, next.Rect))
            {
                next.AppWindow.MoveAndResize(r);
                _frames = 0;
            }

            next.FollowCaret();
            return;
        }

        if (!_front.OnScreen || PopupPlacement.IsWaitingForFrame(_front))
        {
            // Not on screen yet (or only just shown, still cloaked): the one-card cloak-until-drawn show
            _front.SetText(text, caret);
            _front.ShowAbove(anchorX, bandTop);
            _front.FollowCaret();
            return;
        }

        // On screen: lay the new text out on the hidden card to learn the size it needs
        _back.SetText(text, caret);
        var target = _back.Fit(anchorX, bandTop);
        if (SameRect(target, _front.Rect))
        {
            _front.SetText(text, caret);   // same size: in place, nothing moves
            _front.Fit(anchorX, bandTop);
            _front.FollowCaret();
            return;
        }

        StartSwap(_back, target);
    }

    private void StartSwap(PreviewCard next, Windows.Graphics.RectInt32 at)
    {
        _swapping = next;
        _frames = 0;
        PopupPlacement.SetCloaked(next, true);
        next.AppWindow.MoveAndResize(at);
        next.AppWindow.Show(activateWindow: false);
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(next);
        NativeMethods.SetWindowPos(hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0, NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);   // above the card it replaces
        next.FollowCaret();
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, object e)
    {
        if (_swapping is not { } next)
        {
            CompositionTarget.Rendering -= OnRendering;
            return;
        }

        _frames++;
        if (_frames == 3)
        {
            // A Rendering tick comes before its frame is drawn: by the third, the new size has been drawn. It goes on screen over
            // the old card (it covers it when it grew; when it shrank, the old one shows behind it for this one frame).
            PopupPlacement.SetCloaked(next, false);
        }
        else if (_frames >= 4)
        {
            // A frame later the old card goes. Not in the same step: had the two changes reached the screen in separate frames
            // the other way round, there would have been a frame with no card at all.
            CompositionTarget.Rendering -= OnRendering;
            _swapping = null;
            _front.Hide();
            (_front, _back) = (next, _front);
        }
    }

    private static bool SameRect(Windows.Graphics.RectInt32 a, Windows.Graphics.RectInt32 b) =>
        a.X == b.X && a.Y == b.Y && a.Width == b.Width && a.Height == b.Height;

    public bool UnderCursor() => _front.UnderCursor() || (_swapping?.UnderCursor() ?? false);

    public void Hide()
    {
        if (_swapping is { } next)
        {
            CompositionTarget.Rendering -= OnRendering;
            _swapping = null;
            next.Hide();
            PopupPlacement.SetCloaked(next, false);   // so its next show doesn't start cloaked
        }

        _front.Hide();
    }

    public void Close()
    {
        CompositionTarget.Rendering -= OnRendering;
        _swapping = null;
        _front.Close();
        _back.Close();
    }

    /// <summary>One preview card: the wrapped question with a blinking caret where the input box's caret is.</summary>
    private sealed class PreviewCard : Window
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
        private int _caretAt;
        private int _length;

        public PreviewCard()
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
            Closed += (_, _) => _blink.Stop();   // closed at exit without Hide(): the blink kept writing to a closed window (review 11-03)
            _blink.Interval = TimeSpan.FromMilliseconds(530);   // the Windows caret cadence
            _blink.Tick += (_, _) => _caret.Foreground = ReferenceEquals(_caret.Foreground, _caretOn) ? _caretOff : _caretOn;

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            var ex = NativeMethods.Unsigned32(NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE));
            NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, NativeMethods.Signed32(ex | WS_EX_NOACTIVATE));
            AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));
        }

        public event Action? Clicked;

        public bool OnScreen => AppWindow.IsVisible && AppWindow.Position.X > -30000;

        public Windows.Graphics.RectInt32 Rect => new(AppWindow.Position.X, AppWindow.Position.Y, AppWindow.Size.Width, AppWindow.Size.Height);

        public void SetText(string text, int caret)
        {
            var at = Math.Clamp(caret, 0, text.Length);
            _before.Text = text[..at];
            _after.Text = text[at..];
            _caretAt = at;
            _length = text.Length;
            _caret.Foreground = _caretOn;   // solid the instant a key lands, then resume blinking (like a real caret)
            _blink.Start();
        }

        /// <summary>The place and size this text needs above the search box (the window itself isn't moved).</summary>
        public Windows.Graphics.RectInt32 Fit(int anchorX, int bandTop)
        {
            _scroll.MaxHeight = PopupPlacement.MaxHeightAbove(anchorX, bandTop) - HintDip;
            return PopupPlacement.FitAbove(this, _panel, WidthDip, anchorX, bandTop);
        }

        public void ShowAbove(int anchorX, int bandTop)
        {
            _scroll.MaxHeight = PopupPlacement.MaxHeightAbove(anchorX, bandTop) - HintDip;
            PopupPlacement.ShowDrawnAbove(this, _panel, WidthDip, anchorX, bandTop);
        }

        /// <summary>When the prompt is taller than the screen, keep the part around the caret in view (typing usually happens at the end).</summary>
        public void FollowCaret()
        {
            _scroll.UpdateLayout();
            var hidden = _scroll.ExtentHeight - _scroll.ViewportHeight;
            if (hidden <= 0)
            {
                return;
            }

            var ratio = _length == 0 ? 1.0 : (double)_caretAt / _length;
            var target = Math.Clamp((ratio * _scroll.ExtentHeight) - (_scroll.ViewportHeight / 2), 0, hidden);
            _scroll.ChangeView(null, target, null, disableAnimation: true);
        }

        public bool UnderCursor() => AppWindow.IsVisible && PopupPlacement.UnderCursor(this);

        public void Hide()
        {
            _blink.Stop();
            AppWindow.Hide();
        }
    }
}
