using System.Linq;
using System.Numerics;
using IsleBar.App.Interop;
using IsleBar.App.Options;
using IsleBar.Core.Ui;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using static IsleBar.App.Interop.NativeMethods;

namespace IsleBar.App.IslandUi;

/// <summary>
/// The island's expanded presentation for messages (Windows notifications, KakaoTalk): a card that grows out of the pill,
/// shows the sender, message and the app's own icon, then shrinks back into the pill.
/// <para>
/// <b>Front/back deck</b> (user idea 09-30): back-to-back messages stack as a deck — the newest is the full front card, earlier
/// ones peek out behind it. <b>Drag the front card down</b> to spread the deck into a readable list of every message (user idea
/// 09-30: "let me grab and drag to see the ones behind"); a short press instead opens the app. The window is clipped to the
/// deck silhouette (a WinUI window can't be transparent here); a single card or the opened list uses the system's rounded corner.
/// </para>
/// Never takes focus.
/// </summary>
internal sealed class ExpandedNotice : Window
{
    /// <summary>Closes the card and the cards behind it (hidden windows kept the app from ending by itself — review 10-03).</summary>
    public void CloseAll()
    {
        _stayTimer?.Stop();
        _open = false;
        _dismissing++;   // a fade still running must not call Hide on the closed window
        UnhookOutsideClick();   // its system-wide mouse hook otherwise stayed until the process ended
        foreach (var peek in _peeks)
        {
            try
            {
                peek?.Close();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
            }
        }

        Close();
    }

    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const int CardWidthDip = 380;
    private const int Radius = 12;
    private const int Peek = 14;            // how far each card behind sticks out above the one in front (Sonner deck: Y = −14·n)
    private const int InsetPerLevel = 10;   // inset per level ≈ Sonner scale 1−0.05·n on a 380-wide card (≈9.5 each side)
    private const int MaxBehind = 2;        // front + up to two peeking behind
    private const int MaxListHeightDip = 440;   // the opened list caps here and scrolls (grows up from the taskbar; keep it on-screen)
    private const int KeepMessages = 50;    // cap on today's messages the list keeps (RAM is negligible; the list scrolls when it's taller than MaxListHeightDip)
    private const int ExpandDrag = 26;      // drag the front card down this far to open the list
    private static readonly TimeSpan Stay = TimeSpan.FromSeconds(6);
    private static readonly TimeSpan StayExpanded = TimeSpan.FromSeconds(8);

    private readonly Canvas _root;
    private readonly PeekWindow?[] _peeks = new PeekWindow?[MaxBehind];   // the cards behind the front are their own DWM-rounded windows
    private readonly Microsoft.UI.Dispatching.DispatcherQueue _ui = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
    private readonly List<Message> _stack = new();
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _stayTimer;
    private Windows.Graphics.RectInt32 _cardRect;
    private Windows.Graphics.RectInt32 _pill;
    private Border? _front;
    private FrameworkElement? _frontIcon;   // the front card's icon slot, for the flow-into-place animation on open
    private ScrollViewer? _listScroller;    // the opened list's scroller, so the global mouse hook can wheel-scroll it (the window is non-activating)
    private int _behind;
    private bool _open;
    private bool _listOpen;
    private bool _dragging;
    private double _dragStartY;
    /// <summary>The running outside-click hook thread; it owns its hook handle and delegate.</summary>
    private HookThread? _hook;

    private sealed class HookThread
    {
        public uint Id;
        public readonly ManualResetEventSlim Ready = new();
    }

    public ExpandedNotice()
    {
        PopupPlacement.MakeToolPopup(this);
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(false, false);
        }

        _root = new Canvas();
        Content = _root;

        // The window is clipped to the card shape with SetWindowRgn (in ApplyRegion), so the desktop shows around/between the
        // cards — a true transparent WinUI window (DwmExtendFrameIntoClientArea) only rendered an opaque white box here (user 09-30).
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ex = Unsigned32(GetWindowLong(hwnd, GWL_EXSTYLE));
        SetWindowLong(hwnd, GWL_EXSTYLE, Signed32(ex | WS_EX_NOACTIVATE));
        AppWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));

        // Create the peek windows up front (hidden). Creating a window later, mid-Show, pumps the message loop; made once here,
        // they are only positioned later.
        for (var i = 0; i < _peeks.Length; i++)
        {
            _peeks[i] = new PeekWindow();
        }
    }

    /// <summary>One message in the deck. <see cref="Open"/> is what to launch when tapped; <see cref="AppId"/> is the AUMID for the app's real icon. <see cref="When"/> is when it arrived (for ageing the deck).</summary>
    private readonly record struct Message(string Glyph, string Title, string? Body, string? App, string? Open, string? AppId, DateTimeOffset When);

    /// <summary>Show a message growing out of <paramref name="pill"/> (screen pixels); it stacks onto an open deck and shrinks back by itself.</summary>
    public void Show(Windows.Graphics.RectInt32 pill, string glyph, string title, string? message, string? source, string? open, string? appId)
    {
        _pill = pill;
        var now = DateTimeOffset.Now;
        // Keep everything from today, so the opened list is a notification centre you can scroll (user 10-01: "phone-like, endless"),
        // capped at KeepMessages so it never runs away. Anything from a previous day drops off. The deck's peeks still only show the
        // most recent couple behind the front, so a new message never resurfaces old news on the pill.
        _stack.RemoveAll(m => m.When.Date != now.Date);
        var incoming = new Message(glyph, title, message, source, open, appId, now);
        // dedupe by content (a re-read/re-post must not add a duplicate card — box glitched when it did, user 09-30), but the
        // repeat moves to the front: kept in its old place, the front card showed whatever came after it, and a tap opened that (review 10-03)
        _stack.RemoveAll(m => m.Title == title && m.Body == message && m.App == source);
        _stack.Add(incoming);
        if (_stack.Count > KeepMessages)
        {
            _stack.RemoveRange(0, _stack.Count - KeepMessages);
        }

        _dismissing++;   // a fade-out under way must not hide this new message when it finishes

        _stayTimer?.Stop();
        int frontDip;
        if (_listOpen)
        {
            _behind = 0;
            frontDip = BuildList();            // a new message joins the already-open list (one window, no peeks)
        }
        else
        {
            _behind = Math.Min(_stack.Count - 1, MaxBehind);
            frontDip = BuildDeck();            // the front card only; the peeks are their own windows
        }

        Present(frontDip, firstGrow: !_open);
        ArmDismiss(_listOpen ? StayExpanded : Stay);
        _open = true;
    }

    /// <summary>
    /// Places the front card window (just above the pill), positions the peek windows behind it, and springs the front in.
    /// The front is a single DWM-rounded window; each peek is its own DWM-rounded window — so every corner is anti-aliased and
    /// the desktop shows between the cards with no white fringe.
    /// </summary>
    private void Present(int frontHeightDip, bool firstGrow)
    {
        var w = DpiSetup.Px(CardWidthDip);
        var h = DpiSetup.Px(frontHeightDip);
        _cardRect = new Windows.Graphics.RectInt32(
            _pill.X + ((_pill.Width - w) / 2),
            _pill.Y - h - DpiSetup.Px(10),
            w,
            h);
        AppWindow.MoveAndResize(_cardRect);
        ApplyRegion();
        _root.Width = CardWidthDip;
        _root.Height = frontHeightDip;
        _root.UpdateLayout();

        var visual = ElementCompositionPreview.GetElementVisual(_root);
        visual.CenterPoint = new Vector3(CardWidthDip / 2f, frontHeightDip, 0);   // scale from the bottom, where the pill is
        if (firstGrow)
        {
            visual.Opacity = 0;
            visual.Scale = new Vector3(0.86f, 0.86f, 1);
            visual.Offset = new Vector3(0, 16, 0);
            AppWindow.Show(activateWindow: false);
            SpringVector(visual, "Scale", Vector3.One);
            SpringVector(visual, "Offset", Vector3.Zero);
            FadeTo(visual, 1f, 0.14);

            // Matched-geometry feel (research #3): the icon starts small and low — as if it's the little icon from the pill —
            // and springs up into its slot a beat after the card opens, so it reads as the same icon flowing from the bar into the card.
            if (_frontIcon is { } icon)
            {
                var iv = ElementCompositionPreview.GetElementVisual(icon);
                iv.CenterPoint = new Vector3((float)(icon.ActualWidth <= 0 ? 13 : icon.ActualWidth / 2), (float)(icon.ActualHeight <= 0 ? 13 : icon.ActualHeight / 2), 0);
                iv.Scale = new Vector3(0.45f, 0.45f, 1);
                iv.Offset = new Vector3(0, 26, 0);
                DelayedSpring(iv, "Scale", Vector3.One);
                DelayedSpring(iv, "Offset", Vector3.Zero);
            }
        }
        else
        {
            visual.Opacity = 1;
            visual.Offset = Vector3.Zero;
            visual.Scale = new Vector3(0.97f, 0.97f, 1);
            SpringVector(visual, "Scale", Vector3.One);
        }

        PositionPeeks();
        RaiseFrontAbovePeeks();
        HookOutsideClick();   // the card is non-activating, so a global mouse hook (not WinUI pointer events) handles taps on it and clicks away
    }

    /// <summary>Puts each peek window behind the front, offset up and inset, showing only a strip above the front (the rest tucks under it).</summary>
    private void PositionPeeks()
    {
        var dark = IsDark;
        // Show the deepest peek first and the shallowest last, so their natural top-of-band z-order gives depth (k=1 over k=2);
        // the front is then raised over all of them in RaiseFrontAbovePeeks. Pushing peeks *below* the front instead dropped
        // them out of the topmost band and they vanished (09-30) — raise the front, never sink the peeks.
        for (var k = MaxBehind; k >= 1; k--)
        {
            var slot = _peeks[k - 1];
            if (slot is not null && k <= _behind && !_listOpen)
            {
                slot.SetColor(dark, k);
                var inset = DpiSetup.Px(InsetPerLevel * k);
                var rect = new Windows.Graphics.RectInt32(
                    _cardRect.X + inset,
                    _cardRect.Y - DpiSetup.Px(Peek * k),
                    _cardRect.Width - (2 * inset),
                    DpiSetup.Px((Peek * k) + 10));   // the strip above the front + a little to tuck under it
                slot.Place(rect, DpiSetup.Px(Radius), dark);
            }
            else
            {
                slot?.HidePeek();
            }
        }
    }

    /// <summary>Raises the front card over its peek windows (all topmost; the last one raised in the band is on top).</summary>
    private void RaiseFrontAbovePeeks()
    {
        var front = WinRT.Interop.WindowNative.GetWindowHandle(this);
        SetWindowPos(front, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>Lays out just the front card (the peeks are their own windows now) and returns its height (DIP).</summary>
    private int BuildDeck()
    {
        var dark = IsDark;
        _root.Children.Clear();

        var newest = _stack[^1];
        var content = MessageRow(newest, dark);
        content.Width = CardWidthDip;
        content.Measure(new Windows.Foundation.Size(CardWidthDip, double.PositiveInfinity));
        var frontH = (int)Math.Ceiling(content.DesiredSize.Height);

        var frontColor = CardColor(dark);
        _root.Background = new SolidColorBrush(frontColor);   // fills the window; DWM rounds its corners (anti-aliased)
        var front = new Border
        {
            Width = CardWidthDip,
            Height = frontH,
            CornerRadius = new CornerRadius(Radius),
            Background = new SolidColorBrush(frontColor),
            Child = content,
        };
        Canvas.SetLeft(front, 0);
        Canvas.SetTop(front, 0);
        var newestOpen = newest.Open;
        if (!string.IsNullOrWhiteSpace(newestOpen))
        {
            front.Tapped += (_, _) => LaunchAndDismiss(newestOpen!);   // tap opens the app
        }

        _root.Children.Add(front);
        _front = front;
        _frontIcon = content.Tag as FrameworkElement;
        _listScroller = null;   // this is the deck, not the list
        return frontH;
    }

    /// <summary>Lays out the opened list: every message in full, newest on top, each tappable to open its app. Returns total height (DIP).</summary>
    private int BuildList()
    {
        var dark = IsDark;
        _root.Children.Clear();
        _root.Background = new SolidColorBrush(CardColor(dark));   // list is one DWM-rounded card; the background fills it so the corners have no gap
        _front = null;

        var panel = new StackPanel { Padding = new Thickness(0, 4, 0, 4), HorizontalAlignment = HorizontalAlignment.Stretch };
        for (var i = _stack.Count - 1; i >= 0; i--)
        {
            var m = _stack[i];
            var row = MessageRow(m, dark);
            row.HorizontalAlignment = HorizontalAlignment.Stretch;
            row.Background = new SolidColorBrush(Colors.Transparent);   // whole row tappable
            if (!string.IsNullOrWhiteSpace(m.Open))
            {
                row.Tapped += (_, _) => LaunchAndDismiss(m.Open!);
            }

            panel.Children.Add(row);
            if (i > 0)
            {
                panel.Children.Add(new Border
                {
                    Height = 1,
                    Margin = new Thickness(16, 0, 16, 0),
                    Background = new SolidColorBrush(dark ? Color.FromArgb(28, 255, 255, 255) : Color.FromArgb(18, 0, 0, 0)),
                });
            }
        }

        var scroller = new ScrollViewer
        {
            Content = panel,
            MaxHeight = MaxListHeightDip,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,   // a bar appears only when there are more messages than fit
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Enabled,
        };
        _listScroller = scroller;
        var card = new Border
        {
            Width = CardWidthDip,
            CornerRadius = new CornerRadius(Radius),
            Background = new SolidColorBrush(CardColor(dark)),
            Child = scroller,   // outlined by the DWM border; its own border would double the line
        };
        Canvas.SetLeft(card, 0);
        Canvas.SetTop(card, 0);
        _root.Children.Add(card);
        card.Measure(new Windows.Foundation.Size(CardWidthDip, MaxListHeightDip));
        return (int)Math.Ceiling(card.DesiredSize.Height);
    }

    private Border BehindCard(int level, bool dark)
    {
        var top = Peek * (_behind - level);
        var border = new Border
        {
            Width = CardWidthDip - (2 * InsetPerLevel * level),
            Height = (Peek * level) + Radius,
            CornerRadius = new CornerRadius(Radius),
            Background = new SolidColorBrush(dark
                ? Color.FromArgb(255, (byte)(60 - (level * 10)), (byte)(60 - (level * 10)), (byte)(64 - (level * 10)))
                : Color.FromArgb(255, (byte)(232 - (level * 12)), (byte)(232 - (level * 12)), (byte)(236 - (level * 12)))),
            // no border: the region's clipped edge outlines each peeking card
        };
        Canvas.SetLeft(border, InsetPerLevel * level);
        Canvas.SetTop(border, top);
        return border;
    }

    /// <summary>One message's content: the app's real icon (glyph until it loads), sender, message, app name.</summary>
    private static Grid MessageRow(Message m, bool dark)
    {
        var grid = new Grid
        {
            Padding = new Thickness(16, 14, 16, 14),
            RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light,
        };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var iconSlot = new Grid { Width = 26, Height = 26, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 2, 12, 0) };
        var glyph = new FontIcon
        {
            FontFamily = new FontFamily("Segoe Fluent Icons"),
            FontSize = 22,
            Glyph = string.IsNullOrEmpty(m.Glyph) ? "" : m.Glyph,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var image = new Image { Width = 26, Height = 26, Visibility = Visibility.Collapsed };
        iconSlot.Children.Add(glyph);
        iconSlot.Children.Add(image);
        grid.Children.Add(iconSlot);

        AppIcon.Resolve(m.AppId, source =>
        {
            image.Source = source;
            image.Visibility = Visibility.Visible;
            glyph.Visibility = Visibility.Collapsed;
            var iconVisual = ElementCompositionPreview.GetElementVisual(image);
            iconVisual.Opacity = 0;
            FadeTo(iconVisual, 1f, 0.18);
        });

        var text = new StackPanel();
        text.Children.Add(new TextBlock
        {
            Text = m.Title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        if (!string.IsNullOrWhiteSpace(m.Body))
        {
            text.Children.Add(new TextBlock
            {
                Text = m.Body,
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                MaxLines = 4,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 2, 0, 0),
            });
        }

        if (!string.IsNullOrWhiteSpace(m.App))
        {
            text.Children.Add(new TextBlock { Text = m.App, FontSize = 12, Opacity = 0.55, Margin = new Thickness(0, 6, 0, 0) });
        }

        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        grid.Tag = iconSlot;   // so the deck can animate the icon flowing into place when the card grows (matched-geometry feel, research #3)
        return grid;
    }

    // ---- drag the front card down to open the list, or a short press to open the app ----

    private void FrontPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_listOpen || _front is null)
        {
            return;
        }

        _dragStartY = e.GetCurrentPoint(_root).Position.Y;
        _dragging = true;
        _stayTimer?.Stop();
        _front.CapturePointer(e.Pointer);
    }

    private void FrontMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _front is null)
        {
            return;
        }

        var dy = Math.Clamp(e.GetCurrentPoint(_root).Position.Y - _dragStartY, 0, 90);
        Canvas.SetTop(_front, (Peek * _behind) + dy);   // move by layout, not a hand-in composition visual (that surface lingered and covered the list — 09-30)
    }

    private void FrontReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging || _front is null)
        {
            return;
        }

        _dragging = false;
        _front.ReleasePointerCapture(e.Pointer);
        var dy = e.GetCurrentPoint(_root).Position.Y - _dragStartY;

        // A firm pull down also opens the list (kept as a bonus), but dragging a non-activating card is finicky, so the tap
        // below is the reliable way (user 10-01: drag didn't work).
        if (dy >= 40 && _stack.Count > 1 && !_listOpen)
        {
            OpenList();
            return;
        }

        Canvas.SetTop(_front, Peek * _behind);   // otherwise snap back to its resting position
        if (Math.Abs(dy) < 8)
        {
            // A tap: with a deck behind, open the full list; otherwise open the app (a single message with a deep link).
            if (_stack.Count > 1 && !_listOpen)
            {
                OpenList();
                return;
            }

            if (!string.IsNullOrWhiteSpace(_stack[^1].Open))
            {
                LaunchAndDismiss(_stack[^1].Open!);
                return;
            }
        }

        ArmDismiss(Stay);
    }

    /// <summary>Spread the deck into the readable list.</summary>
    private void OpenList()
    {
        _listOpen = true;
        _stayTimer?.Stop();
        Present(BuildList(), firstGrow: false);
        HookOutsideClick();   // once the list is open, a click outside closes it (user 09-30); hooked only now to avoid global-hook lag
        ArmDismiss(StayExpanded);
    }

    /// <summary>Restart the auto-dismiss countdown.</summary>
    private void ArmDismiss(TimeSpan stay)
    {
        _stayTimer?.Stop();
        _stayTimer = _ui.CreateTimer();
        _stayTimer.Interval = stay;
        _stayTimer.IsRepeating = false;
        _stayTimer.Tick += (_, _) => Dismiss();
        _stayTimer.Start();
    }

    /// <summary>Scale down and fade out, then hide and forget the deck.</summary>
    private void Dismiss()
    {
        var generation = ++_dismissing;
        _open = false;   // a message arriving during the fade re-grows the card instead of joining one about to hide (review 10-03)
        var visual = ElementCompositionPreview.GetElementVisual(_root);
        var batch = visual.Compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        FadeTo(visual, 0f, 0.16);
        var scale = visual.Compositor.CreateVector3KeyFrameAnimation();
        scale.InsertKeyFrame(1f, new Vector3(0.92f, 0.92f, 1));
        scale.Duration = TimeSpan.FromSeconds(0.16);
        visual.StartAnimation("Scale", scale);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (generation == _dismissing)
            {
                Hide();
            }
        };
    }

    /// <summary>Bumped by every dismiss and every new message: a fade-out only hides the card if nothing came in meanwhile.</summary>
    private int _dismissing;

    private void LaunchAndDismiss(string open)
    {
        try
        {
            if (open == SystemWatch.Updater.Command)
            {
                SystemWatch.Updater.Start();   // the "update available" card installs it (10-01)
            }
            else if (open == Interop.HookConnector.Command)
            {
                Task.Run(() => Interop.HookConnector.Set(true));   // the Store build's "connect Claude Code / Codex" card
            }
            else if (open == SystemWatch.ToastWatcher.OpenNotificationCentre)
            {
                // a notice whose app can't be opened: Windows' notification centre, where it still is (Win+N — the
                // "ms-actioncenter:" link did nothing on Windows 11, so tapping these cards did nothing — review 10-03)
                Interop.NativeMethods.keybd_event(Interop.NativeMethods.VK_LWIN, 0, 0, IntPtr.Zero);
                Interop.NativeMethods.keybd_event((byte)'N', 0, 0, IntPtr.Zero);
                Interop.NativeMethods.keybd_event((byte)'N', 0, Interop.NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
                Interop.NativeMethods.keybd_event(Interop.NativeMethods.VK_LWIN, 0, Interop.NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(open) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
        }

        _stayTimer?.Stop();
        Hide();
    }

    private void Hide()
    {
        UnhookOutsideClick();
        foreach (var peek in _peeks)
        {
            peek?.HidePeek();
        }

        AppWindow.Hide();
        _open = false;
        _listOpen = false;
        _dragging = false;
        // Keep the stack: the card only collapsed into the pill, it wasn't "read". The next message re-grows the card with these
        // recent ones as peeks behind it. Yesterday's entries are dropped in Show, so the list stays "today" and the deck never resurrects old news.
    }

    // ---- close when the user clicks anywhere outside the card (the card never takes focus, so a global mouse hook is the way) ----
    // The hook runs on its OWN thread with its own message loop, not the UI thread: a low-level mouse hook is called for every
    // mouse event, and on the UI thread (busy rendering the list) that made the whole cursor lag (user 09-30: "the list lags").

    private void HookOutsideClick()
    {
        if (_hook is not null)
        {
            return;
        }

        var owner = new HookThread();
        var thread = new Thread(() =>
        {
            owner.Id = GetCurrentThreadId();
            HookProc proc = MouseProc;   // local and kept alive below: the delegate must outlive the hook
            var handle = SetWindowsHookEx(WH_MOUSE_LL, proc, GetModuleHandle(null), 0);
            owner.Ready.Set();
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            if (handle != IntPtr.Zero)
            {
                UnhookWindowsHookEx(handle);
            }

            GC.KeepAlive(proc);
        })
        { IsBackground = true, Name = "IsleBar outside-click" };
        _hook = owner;
        thread.Start();
    }

    private void UnhookOutsideClick()
    {
        if (_hook is not { } owner)
        {
            return;
        }

        _hook = null;
        owner.Ready.Wait(TimeSpan.FromSeconds(1));   // its thread id is known once the hook is in (posting before that left the thread running)
        PostThreadMessage(owner.Id, WM_QUIT, IntPtr.Zero, IntPtr.Zero);   // ends the hook thread's message loop → it unhooks
    }

    private IntPtr MouseProc(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && wParam == WM_MOUSEWHEEL && _open && _listOpen && _listScroller is not null)
        {
            var wpt = System.Runtime.InteropServices.Marshal.PtrToStructure<POINT>(lParam);
            var r = _cardRect;
            if (wpt.X >= r.X && wpt.X < r.X + r.Width && wpt.Y >= r.Y && wpt.Y < r.Y + r.Height)
            {
                var delta = (short)(System.Runtime.InteropServices.Marshal.ReadInt32(lParam, 8) >> 16);   // MSLLHOOKSTRUCT.mouseData high word
                _ui.TryEnqueue(() => _listScroller?.ChangeView(null, _listScroller.VerticalOffset - delta, null, true));
                return (IntPtr)1;   // handled — don't also scroll whatever is under the list (the window itself takes no wheel input)
            }
        }

        if (code >= 0 && (wParam == WM_LBUTTONDOWN || wParam == WM_RBUTTONDOWN))
        {
            var pt = System.Runtime.InteropServices.Marshal.PtrToStructure<POINT>(lParam);   // MSLLHOOKSTRUCT starts with the screen POINT
            var r = _cardRect;
            var inside = pt.X >= r.X && pt.X < r.X + r.Width && pt.Y >= r.Y && pt.Y < r.Y + r.Height;
            var left = wParam == WM_LBUTTONDOWN;
            // A global hook handles the clicks because the card is a non-activating window — WinUI never routes its own pointer
            // events, so tap/drag on the card itself do nothing (user 10-01). Inside a deck → open the list; outside → dismiss.
            if (_open && !_dragging)
            {
                _ui.TryEnqueue(() =>
                {
                    if (!_open)
                    {
                        return;
                    }

                    if (!inside)
                    {
                        Dismiss();   // click away closes the card/list; the click still reaches whatever is underneath
                    }
                    else if (left && !_listOpen && _stack.Count > 1)
                    {
                        OpenList();   // tap the deck → spread into the full list
                    }
                    else if (left && !_listOpen && !string.IsNullOrWhiteSpace(_stack[^1].Open))
                    {
                        LaunchAndDismiss(_stack[^1].Open!);   // tap a single message → open its app
                    }
                });
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);   // the hook handle argument is ignored
    }

    private const int WH_MOUSE_LL = 14;
    private const uint WM_QUIT = 0x0012;
    private static readonly IntPtr WM_LBUTTONDOWN = 0x0201;
    private static readonly IntPtr WM_RBUTTONDOWN = 0x0204;
    private static readonly IntPtr WM_MOUSEWHEEL = 0x020A;

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public POINT Point;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    private static IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId) => SetWindowsHookExW(idHook, lpfn, hMod, dwThreadId);

    [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? name);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG msg);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG msg);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    /// <summary>
    /// Shapes the window. A single card or the opened list is a plain rounded rectangle: the system rounds it (anti-aliased)
    /// with a hairline border colour. A deck is clipped to the silhouette (the union of the cards' rounded rectangles) so the
    /// cards float over the desktop with the background showing between them (user 09-30 chose the floating look over a panel).
    /// </summary>
    private void ApplyRegion()
    {
        // The front card is always a single rounded rectangle: the system rounds it (anti-aliased) with a hairline border.
        // The cards behind are separate windows (PeekWindow), each rounded the same way — so every corner is smooth and no
        // window background leaks as a white edge (user 09-30: the region silhouette couldn't do both).
        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var dark = IsDark;
        SetWindowRgn(hwnd, IntPtr.Zero, redraw: true);
        var round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));
        var border = dark ? 0x00505055u : 0x00D8D8DEu;
        DwmSetWindowAttributeUInt(hwnd, DWMWA_BORDER_COLOR, ref border, sizeof(uint));
    }

    private static bool IsDark => Application.Current.RequestedTheme == ApplicationTheme.Dark;

    private static Color CardColor(bool dark) => dark ? Color.FromArgb(255, 44, 44, 48) : Color.FromArgb(255, 255, 255, 255);

    private static void SpringVector(Visual visual, string property, Vector3 to)
    {
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = to;
        spring.DampingRatio = 0.8f;   // research (MS Composition Scale/Size rec): damping ~0.8, less wobble than 0.72 = a crisper open
        spring.Period = TimeSpan.FromMilliseconds(48);
        visual.StartAnimation(property, spring);
    }

    /// <summary>A slightly livelier spring that starts a beat late, so the icon visibly flows into place after the card has opened.</summary>
    private static void DelayedSpring(Visual visual, string property, Vector3 to)
    {
        var spring = visual.Compositor.CreateSpringVector3Animation();
        spring.FinalValue = to;
        spring.DampingRatio = 0.7f;
        spring.Period = TimeSpan.FromMilliseconds(58);
        spring.DelayTime = TimeSpan.FromMilliseconds(70);
        spring.DelayBehavior = Microsoft.UI.Composition.AnimationDelayBehavior.SetInitialValueBeforeDelay;
        visual.StartAnimation(property, spring);
    }

    private static void FadeTo(Visual visual, float to, double seconds)
    {
        var fade = visual.Compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1f, to);
        fade.Duration = TimeSpan.FromSeconds(seconds);
        visual.StartAnimation("Opacity", fade);
    }
}
