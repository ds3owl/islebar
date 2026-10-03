using IsleBar.App.Interop;
using IsleBar.App.IslandUi;
using IsleBar.App.Media;
using IsleBar.App.Search;
using IsleBar.App.Supervisor;
using IsleBar.App.Tracking;
using IsleBar.Core.Configuration;
using IsleBar.Core.History;
using IsleBar.Core.Island;
using IsleBar.Core.Launch;
using IsleBar.Core.Localization;
using IsleBar.Core.Models;
using IsleBar.Core.Search;
using IsleBar.Core.Ui;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using WinRT.Interop;

namespace IsleBar.App;

/// <summary>
/// The search bar itself. Almost all logic lives in <c>IsleBar.Core</c> (so it can be tested); this
/// class only attaches to Windows and draws.
///
/// <b>This file has never been built. Needs phase-0 verification on a PC — docs/PHASE0_CHECKLIST.md</b>
///
/// Event handlers are wired <b>only in one place</b>: <see cref="WireEvents"/>.
/// On 09-29 two bugs came from wiring the same event twice, with the second overriding the first.
/// </summary>
public sealed partial class MainWindow : Window
{
    /// <summary>Measured Windows 11 taskbar search box (09-29 capture, 200%): 220x32, pill, vertically centered.</summary>
    private const int BaseWidth = 220;
    private const int BaseHeight = 32;

    private static readonly TimeSpan IslandPollInterval = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan SearchDebounce = TimeSpan.FromMilliseconds(90);

    private readonly int _width = DpiSetup.Px(BaseWidth);
    private readonly int _height = DpiSetup.Px(BaseHeight);

    private readonly ConfigStore _store = new(AppPaths.SettingsFile);
    private readonly ActivityStore _activities = ActivityStore.CreateDefault();
    private readonly DispatcherQueue _ui;

    private readonly IntPtr _hwnd;
    private readonly TaskbarHost _taskbar;
    private readonly SearchBoxTracker _tracker;
    private readonly IslandAnimator _animator;
    private readonly FileSearchWorker _fileSearch;
    private readonly Watchdog _watchdog;

    private IsleBarSettings _settings;
    private LanguageStrings _text;
    private string _language;
    private ThemeColors _theme;
    private HistoryNavigator _history;

    private BarMode _mode = BarMode.Claude;
    // not "Hidden": a first placement of Hidden must still be applied (it compared equal and the window stayed where WinUI put it — review 10-03)
    private BarPlacement _placement = new(PlacementMode.Hide, int.MinValue, int.MinValue);

    private readonly MediaWatcher _media;
    private readonly DownloadWatcher _downloads = new();
    private readonly PhoneDrop _phoneDrop = new();

    /// <summary>Brief notices (charging, Bluetooth, internet, focus, copy, overload, calendar, mic/camera; features added 09-30).</summary>
    private SystemWatch.SystemWatchers? _system;

    /// <summary>While a file is dragged over the pill (blue border).</summary>
    private bool _dropHover;
    private string? _dropNames;   // names of the files being dragged over the pill, for the drop hint
    private GlobalHotkey? _hotkey;
    private IntPtr _previousForeground;
    private string? _historyText;
    private Microsoft.UI.Xaml.Controls.Primitives.FlyoutBase? _entryMenu;
    private Options.OptionsFlyout? _flyout;
    private ResultsWindow? _results;
    private OpenRequest? _openWhenReady;

    // the text of the latest file search sent, and of the one the results list shows — Enter right after editing (before the
    // 90 ms debounce and Everything's answer) opened the old query's first hit, an .exe or .lnk included (review 10-03)
    private string _requestedQuery = string.Empty;
    private string? _shownQuery;
    private Options.SettingsWindow? _settingsWindow;
    private Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap? _claudeGlyph;

    /// <summary>Currently detaching from the taskbar (focus changes during this are not the end of typing).</summary>
    private bool _switching;
    private IslandSnapshot _snapshot = IslandSnapshot.Empty;
    private DispatcherQueueTimer? _islandTimer;
    private DispatcherQueueTimer? _searchTimer;
    private bool _typing;

    public MainWindow()
    {
        InitializeComponent();

        _ui = DispatcherQueue.GetForCurrentThread();
        _hwnd = WindowNative.GetWindowHandle(this);
        _taskbar = new TaskbarHost(_hwnd);

        _settings = _store.Load();
        _language = LanguageResolver.Resolve(_settings.Lang, CurrentUiLanguageId());
        _text = LanguageCatalog.For(_language);
        SystemWatch.Updater.Strings = () => _text;
        _theme = ThemeColors.FromSystem();
        _history = new HistoryNavigator(_settings.History);

        _tracker = new SearchBoxTracker(_width, _height);
        _media = new MediaWatcher();
        _animator = new IslandAnimator([LeadSlot, IslandContent], IslandContent, GlowRing, PillBody);
        _fileSearch = new FileSearchWorker(OnSearchResult);
        _watchdog = new Watchdog(_taskbar, () => ThemeColors.FromSystem().Light, RequestExit,
            _ => _ui.TryEnqueue(Retheme));

        ApplyFonts();
        ApplyTheme();
        LoadAgentMarks();
        UpdateLeadIcon();
        SetMode(HomeMode());   // start in the default mode too (first in Tab order)
        WireEvents();

        // remove the title bar and border — otherwise only a "WinUI Desktop" title bar shows inside the 64px (PC 09-29)
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter frame)
        {
            frame.SetBorderAndTitleBar(false, false);
            frame.IsResizable = false;
            frame.IsMaximizable = false;
            frame.IsMinimizable = false;
        }

        PillRegion.DisableSystemFrame(_taskbar.OuterWindow);
        _taskbar.Attach();
        ApplyNativeSearchBox();
        ApplyRegion();

        // Move the pill the instant the tracker sees the search box move (taskbar reflow), rather than waiting for the next
        // 250ms island tick — this is what makes it follow the icons naturally instead of lagging behind (user 10-01).
        _tracker.OnChanged = () => _ui.TryEnqueue(FollowRealSearchBox);
        _tracker.Start();
        _media.Start();
        _downloads.Start();
        _system = new SystemWatch.SystemWatchers(() => _text);
        _system.Start(WatchOptions());
        OfferToConnectAgents();

        // Ctrl+Alt+C = type from anywhere (same key as the Python version). Switches to the input box even if an island is showing.
        _hotkey = new GlobalHotkey(() => _ui.TryEnqueue(() =>
        {
            _taskbar.TakeFocus();
            BeginTyping();
            Entry.Focus(FocusState.Programmatic);
        }));
        _watchdog.Start();
        StartIslandPolling();
        _ = UpdateModelAliasesAsync();
    }

    // ---------------- Event wiring (one place only) ----------------

    private void WireEvents()
    {
        Entry.GotFocus += (_, _) =>
        {
            // A right click on the idle pill also focuses the input box; starting to type there swapped in the box's
            // copy/paste menu, so right click showed "Paste" instead of our menu (seen 10-02). Right click never starts typing.
            if (!_menuOpen && DateTimeOffset.UtcNow - _rightPressAt > TimeSpan.FromMilliseconds(500))
            {
                BeginTyping();   // when the right-click menu closed and the window came forward, the input box got focus and typing started (measured on PC 09-30)
            }
        };
        // End of typing = the input box loses focus or the window is deactivated. But ignore transient focus changes while detaching
        // (otherwise: click → detach → focus wobble → immediately reattached, measured on PC 09-29).
        // End of typing only watches for "the window was pushed away by another window". If we also watched the input box losing focus,
        // pressing ⌄ while typing (focus moves to the button) ended typing, ⌄ vanished, and on release there was nothing to click so settings never opened (user feedback 09-30).
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated && !_switching)
            {
                // A click on a result / the preview is on its way — what it does ends typing itself (10-03). Not tied to the button
                // state: a touchpad tap arrives as press+release together, so the button already reads "up" here (review 10-03).
                // Alt+Tab with the mouse resting over the preview, or a click that ends up doing nothing (padding, a file that's
                // gone), used to keep typing on for good — the re-check ends it.
                if (_typing && _compose is not null)
                {
                    return;   // the compose box is taking over the question (Shift+Enter, a pasted block) — it ends typing when it closes
                }

                if (_typing && (_results?.UnderCursor() == true || _preview?.UnderCursor() == true))
                {
                    _ui.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, ScheduleTypingRecheck);
                    return;
                }

                EndTyping();
            }
        };
        // the input box swallows ↑↓ and Tab first (caret/focus movement), so catch them in the preview stage
        Entry.PreviewKeyDown += OnEntryKeyDown;
        HookCompose();
        HookFileDrop();
        foreach (var button in new UIElement[] { ChevronButton, PrevButton, PlayButton, NextButton, PauseButton, DismissButton })
        {
            PressFeedback.Attach(button);   // pressing makes it sink in and spring back (09-30 design M5)
        }

        // pill corner = half the height (height can vary with DPI and taskbar size)
        Root.SizeChanged += (_, _) =>
        {
            var r = Math.Max(1, Root.ActualHeight / 2);
            PillSurface.CornerRadius = new CornerRadius(r);
            GlassBorder.CornerRadius = new CornerRadius(r);
            GlowRing.CornerRadius = new CornerRadius(Math.Max(0, r - 0.5));
        };
        Entry.TextChanged += (_, _) =>
        {
            UpdateTabHint();
            // When a person types, forget the history position. If the text was inserted from history, keep it — WinUI sends this notification
            // later and separately, so an "inserting" flag can't tell them apart (measured on PC 09-29) → compare against the inserted text instead.
            if (Entry.Text != _historyText)
            {
                _history.Reset();
                _historyText = null;
            }

            if (_mode == BarMode.Files)
            {
                ScheduleSearch();
            }
        };

        // a child window doesn't get focus from a normal click, so take it explicitly
        // handledEventsToo so we get it even if the input box handles the click first. After Esc the input box still holds XAML focus,
        // so GotFocus doesn't fire again; treat the click itself as the start of typing (measured on PC 09-29).
        Root.AddHandler(UIElement.PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.GetCurrentPoint(Root).Properties.IsRightButtonPressed)
            {
                _rightPressAt = DateTimeOffset.UtcNow;
            }

            if (!e.GetCurrentPoint(Root).Properties.IsLeftButtonPressed   // right click is the menu (RightTapped)
                || IsInside(e.OriginalSource as DependencyObject, ChevronButton)
                || IsInside(e.OriginalSource as DependencyObject, HoverActions))
            {
                return;   // ⌄ and the island buttons (⏮⏯⏭) do their own job (not the start of typing)
            }

            if (!_typing && TryIslandAction())
            {
                return;   // clicked the island and went to that task (not the start of typing)
            }

            _taskbar.TakeFocus();
            BeginTyping();
            Entry.Focus(FocusState.Programmatic);
        }), handledEventsToo: true);
        // Right click = dismiss an island item (cancel a timer, etc.) · turn off the search bar
        // The input box grabs right clicks first (its own copy/paste menu), so receive them with handledEventsToo.
        // While typing, use the input box's menu as-is; otherwise use our menu — and disable the input box menu then (measured on PC 09-29).
        _entryMenu = Entry.ContextFlyout;
        Entry.ContextFlyout = null;
        Root.AddHandler(UIElement.RightTappedEvent, new RightTappedEventHandler((_, e) =>
        {
            if (_typing)
            {
                return;
            }

            e.Handled = true;
            ShowContextMenu();
        }), handledEventsToo: true);
        Root.PointerEntered += (_, _) => SetHover(true);
        Root.PointerExited += (_, _) => SetHover(false);

        ChevronButton.Click += (_, _) => ShowQuickOptions();
        PlayButton.Click += (_, _) => SendMediaCommand(MediaCommand.PlayPause);
        PrevButton.Click += (_, _) => SendMediaCommand(MediaCommand.Previous);
        NextButton.Click += (_, _) => SendMediaCommand(MediaCommand.Next);
        PauseButton.Click += (_, _) => ToggleTimerPause();
        DismissButton.Click += (_, _) => DismissPrimary();

        Closed += (_, _) => Cleanup();

        // Remove the WinUI input box clear (X) button. Visual state changes toggle its Visibility, so pin its width to 0 instead.
        Entry.Loaded += (_, _) =>
        {
            if (FindChild(Entry, "DeleteButton") is FrameworkElement delete)
            {
                delete.MaxWidth = 0;
                delete.Margin = new Thickness(0);
            }
        };
    }

    private static bool IsInside(DependencyObject? element, DependencyObject container)
    {
        for (var node = element; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node == container)
            {
                return true;
            }
        }

        return false;
    }

    private static DependencyObject? FindChild(DependencyObject parent, string name)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is FrameworkElement { Name: var n } && n == name)
            {
                return child;
            }

            if (FindChild(child, name) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // ---------------- Input ----------------

    /// <summary>
    /// When typing starts, <b>detach</b> from the taskbar. Typing while attached tangles IME/focus
    /// with Explorer and can hang it (09-29 AppHangXProc).
    /// </summary>
    private void BeginTyping()
    {
        if (_typing)
        {
            return;
        }

        var before = Interop.NativeMethods.GetForegroundWindow();
        _previousForeground = before != _taskbar.OuterWindow ? before : IntPtr.Zero;   // window to give focus back to when closed with Esc
        _typing = true;
        _switching = true;
        Entry.ContextFlyout = _entryMenu;   // copy/paste menu while typing
        _taskbar.Detach(Spot.X, Spot.Y, _width, _height);
        // keep the pill shape after detaching — without it a square window covers the real search box and feels like "a separate window opened" (PC 09-29)
        ApplyRegion();
        IslandContent.Visibility = Visibility.Collapsed;
        Entry.Visibility = Visibility.Visible;

        RenderIsland();   // clear the island and set the left icon to the Claude/Codex icon (09-29: the timer icon was left behind)
        PaintFill();      // slightly brighter while typing

        // match the IME composition text size to the input box
        ImeFont.Apply(_hwnd, UiFonts.Entry(_language), DpiSetup.Px(14));

        // only re-enable "end of typing" detection after returning the focus shaken by detaching to the input box
        _ui.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            Entry.Focus(FocusState.Programmatic);
            _switching = false;
        });
        UpdateTabHint();
    }

    /// <summary>When typing ends, go back into the taskbar.</summary>
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _typingRecheck;

    /// <summary>After a click on a popup kept typing on: end it if the bar is no longer in front and nothing took the typing over.</summary>
    private void ScheduleTypingRecheck()
    {
        _typingRecheck ??= _ui.CreateTimer();
        _typingRecheck.Interval = TimeSpan.FromSeconds(1.5);
        _typingRecheck.IsRepeating = false;
        _typingRecheck.Tick -= OnTypingRecheck;
        _typingRecheck.Tick += OnTypingRecheck;
        _typingRecheck.Start();
    }

    private void OnTypingRecheck(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
    {
        if (_typing && NativeMethods.MouseButtonDown())
        {
            sender.Start();   // still holding the button on a popup (scrolling the preview, say) — look again later
            return;
        }

        if (_typing && _compose is null && !_menuOpen && NativeMethods.GetForegroundWindow() != _taskbar.OuterWindow)
        {
            EndTyping();
        }
    }

    private void EndTyping()
    {
        _openWhenReady = null;   // cancelled before the results came: nothing opens later (review 10-03)
        if (!_typing)
        {
            return;
        }

        _typing = false;
        if (_pendingTimer is not null)
        {
            _pendingTimer = null;   // Esc / clicking away = keep the running timer
            Entry.PlaceholderText = Placeholders.For(_mode, _settings.Agent);   // SetMode below skips this when the mode is unchanged (code review 10-01)
        }
        PaintFill();
        Entry.ContextFlyout = null;         // after typing, right click = our menu
        FocusSink.Focus(FocusState.Programmatic);   // move focus out of the input box so no blinking caret is left behind
        Entry.Text = string.Empty;
        _history.Reset();
        CloseResults();
        SetMode(HomeMode());   // after typing, return to the default mode (first in Tab order), same as the Python version's blur
        if (_taskbar.Attach())
        {
            PlaceOnTaskbar();
            ApplyRegion();
        }

        RenderIsland();
        UpdateTabHint();
        _preview?.Hide();
    }

    private void OnEntryKeyDown(object sender, KeyRoutedEventArgs e)
    {
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Tab:
                ToggleMode();
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter when IsShiftDown() && _mode == BarMode.Claude:
                OpenCompose(Entry.Text + "\n");   // newline = go to the long-question compose window (09-30)
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Enter:
                OnEnter(reveal: IsControlDown());
                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Escape:
                Entry.Text = string.Empty;
                EndTyping();
                if (_previousForeground != IntPtr.Zero)
                {
                    ForegroundHelper.Bring(_previousForeground);   // back to the window that was in use before pressing (same as the Python version)
                }

                e.Handled = true;
                break;

            case Windows.System.VirtualKey.Up:
                e.Handled = MoveSelection(-1);
                break;

            case Windows.System.VirtualKey.Down:
                e.Handled = MoveSelection(+1);
                break;
        }
    }

    // ---------------- Long-question compose window ----------------

    private ComposeWindow? _compose;

    /// <summary>Text from a compose window closed with Esc — continued the next time it opens.</summary>
    private string? _composeDraft;

    /// <summary>IME composition in progress, e.g. Korean (moving the window now breaks the composed character).</summary>
    private bool _imeComposing;

    private static bool IsShiftDown()
        => Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    /// <summary>
    /// Long questions go to the compose window (user feedback 09-30: first prompts are long but only a short slice was visible, which was painful).
    /// Opens on: Shift+Enter · pasting multiple lines · text overflowing the input box (only once composition has finished).
    /// </summary>
    private void HookCompose()
    {
        Entry.TextCompositionStarted += (_, _) => _imeComposing = true;
        Entry.TextCompositionEnded += (_, _) =>
        {
            _imeComposing = false;
            _ui.TryEnqueue(OpenComposeIfOverflowing);
        };
        Entry.TextChanged += (_, _) => OpenComposeIfOverflowing();
        Entry.SelectionChanged += (_, _) => { if (_preview is not null) { OpenComposeIfOverflowing(); } };   // move the preview's blinking caret when the input caret moves (arrows/click), not only when the text changes

        // a single-line input box keeps only the first line of a multi-line paste → paste manually, and go to the compose window if multi-line
        Entry.Paste += async (_, e) =>
        {
            if (_mode != BarMode.Claude)
            {
                return;
            }

            e.Handled = true;
            string clip;
            try
            {
                var content = Windows.ApplicationModel.DataTransfer.Clipboard.GetContent();
                if (!content.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.Text))
                {
                    return;
                }

                clip = await content.GetTextAsync();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException)
            {
                return;
            }

            clip = clip.TrimEnd('\r', '\n') is var trimmed && !trimmed.Contains('\n') && !trimmed.Contains('\r') ? trimmed : clip;   // one line copied with its newline
            var start = Entry.SelectionStart;
            var merged = Entry.Text[..start] + clip + Entry.Text[(start + Entry.SelectionLength)..];
            if (clip.Contains('\n') || clip.Contains('\r'))
            {
                OpenCompose(merged);
                return;
            }

            Entry.Text = merged;
            Entry.SelectionStart = start + clip.Length;
        };
    }

    private PromptPreviewWindow? _preview;

    /// <summary>
    /// A long question overflowing the input box is shown in full above it (focus stays put so typed characters aren't lost).
    /// Left alone during IME composition; checked again once composition ends.
    /// </summary>
    private void OpenComposeIfOverflowing()
    {
        var show = _mode == BarMode.Claude && _typing && _compose is null && Entry.Text.Length >= 10 && Overflows();
        if (!show)
        {
            _preview?.Hide();
            return;
        }

        if (_imeComposing && _preview is null)
        {
            return;
        }

        if (_preview is null)
        {
            _preview = new PromptPreviewWindow();
            AppFonts.Apply(_preview, _language);
            _preview.Clicked += () =>
            {
                // Clicking the preview used to end typing and lose the question (10-03). End typing first, then open the compose box:
                // ending it afterwards (the bar's own deactivation) put the bar back in the taskbar, which took focus from the new
                // compose box and closed it at once (log 10-03).
                var text = Entry.Text;
                TaskbarHost.Log($"preview clicked: typing={_typing} len={text.Length}");
                EndTyping();
                OpenCompose(text);
            };
        }

        _preview.Show(Entry.Text, Entry.SelectionStart, PopupAnchorX, TaskbarTop());
    }

    private bool Overflows()
    {
        var probe = new Microsoft.UI.Xaml.Controls.TextBlock { Text = Entry.Text, FontSize = Entry.FontSize, FontFamily = Entry.FontFamily, CharacterSpacing = Entry.CharacterSpacing };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width > Entry.ActualWidth - 16;
    }

    private void OpenCompose(string text)
    {
        if (_compose is not null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(text) && !string.IsNullOrEmpty(_composeDraft))
        {
            text = _composeDraft;
        }

        var opening = text;
        _preview?.Hide();

        _compose = new ComposeWindow(
            () =>
            {
                // the user kept typing in the input box while the window was opening — append whatever was typed after the handed-over text
                // (with a newline in between for Shift+Enter). If it differs from the input box (paste, continued draft), keep the handed-over text as-is.
                var typed = Entry.Text;
                Entry.Text = string.Empty;   // the text moved to the compose window
                var newline = opening.EndsWith('\n');
                var basis = newline ? opening[..^1] : opening;
                return basis.Length > 0 && typed.StartsWith(basis, StringComparison.Ordinal)
                    ? basis + (newline ? "\n" : string.Empty) + typed[basis.Length..]
                    : opening;
            },
            Placeholders.For(_mode, _settings.Agent),
            submit: question =>
            {
                _composeDraft = null;
                if (!Launch(question))
                {
                    _composeDraft = question;   // not started: the next compose box opens with it again
                }
            },
            cancel: draft => _composeDraft = string.IsNullOrWhiteSpace(draft) ? null : draft);
        _compose.Closed += (_, _) =>
        {
            _compose = null;
            if (_typing && !_watchdog.Quitting)   // not while exiting: re-attaching to the taskbar then is what ReleaseForClose avoids
            {
                EndTyping();   // the bar stayed in typing mode while the compose box had the question
            }

            RenderIsland();   // back to the status display
        };
        AppFonts.Apply(_compose, _language);
        _compose.ShowAbove(PopupAnchorX, TaskbarTop());
        RenderIsland();
    }

    /// <summary>In file mode, move through results; in Claude mode, scroll through previous questions.</summary>
    private bool MoveSelection(int delta)
    {
        if (_mode == BarMode.Files)
        {
            return MoveResultSelection(delta);
        }

        if (_mode == BarMode.Web)
        {
            return true;
        }

        var text = _history.Move(delta, Entry.Text);
        if (text is null)
        {
            return true;      // no history: do nothing (just stop the caret from moving)
        }

        // inserting history is not "typing" — if Reset ran here, ↑ would never move past the most recent entry (measured on PC 09-29)
        _historyText = text;
        Entry.Text = text;
        Entry.SelectionStart = text.Length;
        return true;
    }

    private void OnEnter(bool reveal)
    {
        // "Replace the running timer?" is showing in the input box: Enter on it confirms (typing something else drops the question)
        if (_pendingTimer is { } pending)
        {
            _pendingTimer = null;
            if (Entry.Text.Trim().Length == 0)
            {
                ReplaceTimer(pending);
                EndTyping();
                return;
            }

            Entry.PlaceholderText = Placeholders.For(_mode, _settings.Agent);
        }

        if (_mode == BarMode.Files)
        {
            OpenSelectedFile(reveal);
            return;
        }

        if (_mode == BarMode.Web)
        {
            OpenWebSearch(Entry.Text);
            return;
        }

        var question = Entry.Text.Trim();

        // typing e.g. "25min" starts a timer — only when there's a unit and the whole text is a duration (so questions aren't hijacked)
        if (TimerParser.TryParse(question, out var duration))
        {
            if (StartTimer(TimerParser.ToActivity(duration, DateTimeOffset.UtcNow)))
            {
                EndTyping();
            }

            return;
        }

        if (!Launch(question))
        {
            EndTyping();   // the red notice shows on the pill (hidden while typing); ↑ brings the question back
        }
    }

    /// <summary>
    /// The agent couldn't be started: say so on the pill and leave the question where it was. It used to be replaced by the error
    /// text — the question was lost, and Enter then sent the error as a question (review 10-03).
    /// </summary>
    private void ShowLaunchFailure(string message, string question)
    {
        TaskbarHost.Log("launch failed: " + message);
        if (question.Length > 0)
        {
            _store.Update((IsleBarSettings s) =>
            {
                s.History = QuestionHistory.Add(s.History, question);
                return true;
            });
            _settings.History = QuestionHistory.Add(_settings.History, question);
            _history = new HistoryNavigator(_settings.History);
        }

        var notice = IsleBar.Core.SystemWatch.SystemNotice.Make(_text.FormatLaunchFail(message), "\uE7BA", DateTimeOffset.UtcNow, urgent: true);
        notice.State = "error";   // red, like other things that need fixing
        _activities.Write("islebar_launch", notice);
    }

    /// <returns>Whether the agent was started (false: the question is kept for another try).</returns>
    private bool Launch(string question)
    {
        var profile = AgentProfiles.Get(_settings.Agent);
        var args = profile.BuildArgs(ClaudeExecutable.Resolve(profile), question, _settings.ValuesFor(_settings.Agent)).ToList();
        if ((args[0].EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || args[0].EndsWith(".bat", StringComparison.OrdinalIgnoreCase))
            && File.Exists(args[0]))   // not installed at all: starting it below says so
        {
            // npm's codex.cmd: a .cmd runs through cmd.exe, which re-reads the prompt — "&" ran other commands, %NAME% was replaced
            // and only the first line arrived (review 10-03). Start node with the CLI's script instead; never through cmd.exe.
            if (IsleBar.Core.Launch.NpmShim.Unwrap(args[0], File.Exists, File.ReadAllText, Environment.GetEnvironmentVariable("PATH")) is not { } direct)
            {
                ShowLaunchFailure($"{Path.GetFileName(args[0])}: node / script not found", question);
                return false;
            }

            args = [.. direct, .. args.Skip(1)];
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string workDir;
        if (Directory.Exists(_settings.Folder))
        {
            workDir = _settings.Folder;
        }
        else
        {
            // Claude Code doesn't persist trust for the home folder, so the Security guide shows every time → use a dedicated folder
            workDir = Path.Combine(home, FolderTrust.DefaultFolderName);
            Directory.CreateDirectory(workDir);
        }

        // Only IsleBar's own folder is marked trusted: for a folder the person picked (a repository just cloned, say) the agent's
        // own trust question stays — it guards against that folder's hooks and settings (review 10-03).
        if (string.Equals(Path.GetFullPath(workDir).TrimEnd('\\'), Path.Combine(home, FolderTrust.DefaultFolderName), StringComparison.OrdinalIgnoreCase))
        {
            if (_settings.Agent != AgentKind.Codex)
            {
                FolderTrust.Ensure(Path.Combine(home, ".claude.json"), workDir);
            }
            else
            {
                var codexHome = Environment.GetEnvironmentVariable("CODEX_HOME") is { Length: > 0 } custom ? custom : Path.Combine(home, ".codex");
                FolderTrust.EnsureCodex(Path.Combine(codexHome, "config.toml"), workDir);
            }
        }

        try
        {
            var startInfo = new System.Diagnostics.ProcessStartInfo(args[0])
            {
                WorkingDirectory = workDir,
                UseShellExecute = false,
            };
            // if the bar was launched from inside another Claude session, that session marker is inherited and the new session's history saving is disabled
            // ("inherited CLAUDE_CODE_CHILD_SESSION marker", measured on PC 09-29) → don't pass session variables through.
            // A variable the person set themselves (user or system environment — CLAUDE_CODE_GIT_BASH_PATH, Bedrock / Vertex
            // switches, …) is theirs and stays; the bar used to drop those too, and claude failed only when started from it (review 10-03).
            foreach (var key in startInfo.Environment.Keys
                         .Where(k => (k.StartsWith("CLAUDECODE", StringComparison.OrdinalIgnoreCase)
                                      || k.StartsWith("CLAUDE_CODE_", StringComparison.OrdinalIgnoreCase)
                                      || k is "CLAUDE_PID" or "CLAUDE_EFFORT")
                                     && Environment.GetEnvironmentVariable(k, EnvironmentVariableTarget.User) is null
                                     && Environment.GetEnvironmentVariable(k, EnvironmentVariableTarget.Machine) is null)
                         .ToList())
            {
                startInfo.Environment.Remove(key);
            }

            // Launching into a fresh console (a GUI app has no TERM/COLORTERM to inherit) made the agent decide there was no colour
            // support and print monochrome — ugly (user 10-01). Force colour on; the CLI turns on the console's VT processing itself,
            // so the codes render. FORCE_COLOR covers Claude (Node), CLICOLOR_FORCE covers Codex, COLORTERM advertises truecolor.
            startInfo.Environment["FORCE_COLOR"] = "3";
            startInfo.Environment["CLICOLOR_FORCE"] = "1";
            startInfo.Environment["COLORTERM"] = "truecolor";

            foreach (var arg in args.Skip(1))
            {
                startInfo.ArgumentList.Add(arg);   // don't leave quoting to the shell
            }

            System.Diagnostics.Process.Start(startInfo);
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            ShowLaunchFailure(ex.Message, question);
            return false;
        }

        if (question.Length > 0)
        {
            _store.Update((IsleBarSettings s) =>
            {
                s.History = QuestionHistory.Add(s.History, question);
                return true;
            });
            // in place: an open settings window holds this same object — replacing it with a fresh load made every switch flipped
            // there afterwards change a copy nobody saved (review 10-03)
            _settings.History = QuestionHistory.Add(_settings.History, question);
            _history = new HistoryNavigator(_settings.History);
            // the model list check writes these to the file in the background; keep them current in memory too, or the next
            // settings save wrote the old ones back (review 10-03)
            TakeBackgroundModelLists();
        }

        EndTyping();
        return true;
    }

    /// <summary>Tab: Claude → files → web → Claude (files skipped if file search is off).</summary>
    private void ToggleMode() => SetMode(BarModes.Next(_mode, UsableModes(), filesOn: true));

    /// <summary>Only enabled modes, in Tab order (reflects modes removed in settings and the file-search switch).</summary>
    private List<BarMode> UsableModes() => BarModes.Usable(ModeOrder(), _settings.Files, _settings.ModesOff);

    private List<BarMode> ModeOrder() => BarModes.Normalize(_settings.ModeOrder);

    /// <summary>Default mode = first in Tab order (files skipped if file search is off).</summary>
    private BarMode HomeMode() => UsableModes()[0];

    private void SetMode(BarMode mode)
    {
        _openWhenReady = null;
        if (mode == _mode)
        {
            return;
        }

        _mode = mode;
        _history.Reset();
        Entry.PlaceholderText = Placeholders.For(_mode, _settings.Agent);
        UpdateLeadIcon();
        UpdateTabHint();
        if (_mode == BarMode.Files)
        {
            ScheduleSearch();
        }
        else
        {
            CloseResults();
        }
    }

    /// <summary>Web mode Enter: open search results in the default browser (engine from settings — auto means the per-language default).</summary>
    private void OpenWebSearch(string query)
    {
        if (WebSearch.UrlFor(WebSearch.Resolve(_settings.WebEngine, _language), query) is not { } url)
        {
            return;
        }

        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url.AbsoluteUri) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskbarHost.Log("file search: open failed — " + ex.Message);   // used to fail without a trace (10-03)
            return;
        }

        Entry.Text = string.Empty;
        EndTyping();
    }

    // ---------------- Island ----------------

    private void StartIslandPolling()
    {
        _islandTimer = _ui.CreateTimer();
        _islandTimer.Interval = IslandPollInterval;
        _islandTimer.Tick += (_, _) =>
        {
          try
          {
            RetryAttachIfNeeded();
            FollowRealSearchBox();
            RefreshBesideColor();
            if (DateTimeOffset.UtcNow - _lastPrune > TimeSpan.FromMinutes(1))
            {
                _lastPrune = DateTimeOffset.UtcNow;
                _activities.PruneDead(_lastPrune);   // so finished timer files don't pile up
            }

            var items = _activities.Read(DateTimeOffset.UtcNow).ToList();
            AdvancePomodoros(items);
            ChimeFinishedTimers(items);
            DismissSeenAgents(items);   // first, so a completion whose terminal is already gone is cleared without a chime
            ChimeFinishedAgents(items);
            if (_media.Current is { } music)
            {
                items.Add(music);
            }

            items.AddRange(_downloads.Current);   // browser downloads (in progress / just finished)
            items.AddRange(_phoneDrop.Current(items));   // dropped files being sent to the phone
            if (_system is not null)
            {
                items.AddRange(_system.Current);   // brief notices
            }

            UpdatePrivacyDot();
            EndStaleHover();

            var next = IslandSelector.Select(items);
            _snapshot = next;
            // A task finished while something else (music) holds the pill → remember it so the thick completion border still lights,
            // without taking the song off the pill (user 10-01). When the done is itself the primary, UpdateGlow handles it directly.
            _backgroundDone = items.FirstOrDefault(i => i.Kind == ActivityKind.AgentDone && i.SourcePath is not null);
            _backgroundDoneKey = _backgroundDone is { SourcePath: { } donePath } ? "done|" + donePath : null;
            ExpandIfMessage(items);   // feed EVERY new message notice to the expanded card, not just the primary one (bursts dropped middles — user 09-30)
            var change = _animator.Classify(next);   // the animator decides whether it's a new item (kind/source/track)
            if (_swapping)
            {
                return;   // previous content is still leaving — draw the latest _snapshot when it's done
            }

            if (!_typing && change is IslandAnimator.Change.Swapped or IslandAnimator.Change.Cleared)
            {
                // the previous content fades out first (0.12s), then the new content comes in (09-30 design M3)
                _swapping = true;
                _animator.Exit(() =>
                {
                    _swapping = false;
                    RenderIsland();
                    if (_snapshot.IsActive)
                    {
                        _animator.Enter();
                    }
                    else
                    {
                        _animator.Restore();
                    }
                });
                return;
            }

            // Music is the long-running island and its only per-tick change is the progress bar, which now advances ~once a second
            // (the media watcher is event-driven). So skip its re-render when nothing visible changed — the title marquee keeps
            // scrolling on the compositor regardless. Timers still draw every tick for a smooth ring; a quick sig catches the rest.
            var musicPrimary = change is IslandAnimator.Change.None && !_hovering && !_typing
                ? _snapshot.Primary is { Kind: ActivityKind.Music } m ? m : null
                : null;
            if (musicPrimary is not null && MusicRenderSig(musicPrimary) == _lastMusicSig)
            {
                // nothing visible changed for the music island this tick — don't re-render
            }
            else
            {
                _lastMusicSig = musicPrimary is null ? null : MusicRenderSig(musicPrimary);
                RenderIsland();
            }

            if (change is IslandAnimator.Change.Appeared or IslandAnimator.Change.Swapped)
            {
                _animator.Enter();
            }
          }
          catch
          {
            // Never let one bad tick kill the island timer — a thrown tick stops the DispatcherQueueTimer for good and the
            // whole pill/card freezes on the previous frame (the Translation.Y bug did exactly this, 09-30). Swallow and retry next tick.
          }
        };
        _islandTimer.Start();
    }

    /// <summary>
    /// Follows the position computed in the background. <b>No UIA calls here</b> —
    /// it only reads one value (so it never waits on Explorer).
    /// </summary>
    private void FollowRealSearchBox()
    {
        var placement = _tracker.Current;
        if (placement == _placement)
        {
            return;
        }

        var wasOver = _placement.Mode == PlacementMode.OverRealSearchBox;
        _placement = placement;
        Interop.TaskbarHost.Log($"follow: {placement.Mode} {placement.X},{placement.Y} size {_width}x{_height} typing={_typing}");
        if (wasOver != (placement.Mode == PlacementMode.OverRealSearchBox) && _taskbar.IsAttached)
        {
            ApplyNativeSearchBox();   // the pill moved onto / away from the real box
        }
        if (placement.Mode == PlacementMode.Hide)
        {
            // don't use AppWindow.Show/Hide on a taskbar child window (PC 09-29: Show hung and the position was never set)
            Interop.NativeMethods.ShowWindow(_taskbar.OuterWindow, Interop.NativeMethods.SW_HIDE);
            return;
        }

        Interop.NativeMethods.ShowWindow(_taskbar.OuterWindow, Interop.NativeMethods.SW_SHOWNOACTIVATE);
        Root.Background = BrushOf(TaskbarColorBeside());   // when the position changes, re-read the adjacent taskbar color too
        if (!_typing)
        {
            _taskbar.PlaceAt(placement.X, placement.Y, _width, _height);
        }
    }

    private DateTimeOffset _lastAttachTry = DateTimeOffset.MinValue;

    /// <summary>When the right mouse button last went down on the pill (a right click must not start typing).</summary>
    private DateTimeOffset _rightPressAt = DateTimeOffset.MinValue;

    private bool _nativeShown = true;

    /// <summary>
    /// Setting "hide the real search box (native)": when on, inject the module into Explorer (not again for the same Explorer — anew if Explorer restarts),
    /// when off, restore. Called every time we attach to the taskbar and whenever settings change. Injecting takes a moment, so it runs in the background.
    /// </summary>
    private void ApplyNativeSearchBox()
    {
        // Only while the pill sits over the real box: with search as an icon (or turned off) the pill moves aside, and hiding the
        // icon then left an empty gap where the person's search icon was (review 10-03)
        // and only while attached: when attaching keeps failing the real box is shown as a fallback, and any settings change
        // used to hide it again behind a pill that isn't there (review 10-03)
        if (_settings.NativeSearchBox && _placement.Mode == PlacementMode.OverRealSearchBox && _taskbar.IsAttached)
        {
            _nativeShown = false;
            NativeSearchBox.RequestHide();
        }
        else if (!_nativeShown)
        {
            _nativeShown = true;
            NativeSearchBox.Show();
        }
    }

    // ---------------- Scrolling title · now-playing bars ----------------

    /// <summary>Scroll speed (DIP/s) and the pause at each lap — relaxed, like music apps.</summary>
    private const double MarqueeSpeed = 30;
    private static readonly TimeSpan MarqueeHold = TimeSpan.FromSeconds(2);

    /// <summary>Gap between the two title copies (must equal IslandTextCopy's left margin in XAML so the seam is invisible).</summary>
    private const double MarqueeGap = 40;

    private string? _marqueeText;
    private bool _equalizerOn;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _meterTimer;
    private readonly AudioMeter _audioMeter = new();
    private readonly IsleBar.Core.Ui.LevelBars _levelBars = new();

    private void StopLoopBars()
    {
        _equalizerOn = false;
        foreach (var bar in new[] { Eq0, Eq1, Eq2, Eq3 })
        {
            Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(bar).StopAnimation("Scale");
        }
    }

    /// <summary>
    /// Bars follow the sound (setting "music bars follow the sound"): 30 times a second the four band levels from the
    /// loopback capture become heights via Core's LevelBars (fast rise, slow fall). Only runs while the music bars show.
    /// </summary>
    private void StartMeterBars()
    {
        _meterTimer = _ui.CreateTimer();
        _meterTimer.Interval = TimeSpan.FromMilliseconds(33);
        _meterTimer.Tick += (_, _) =>
        {
            var heights = _levelBars.Update(_audioMeter.Levels());
            var bars = new[] { Eq0, Eq1, Eq2, Eq3 };
            for (var i = 0; i < bars.Length; i++)
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(bars[i]);
                visual.CenterPoint = new System.Numerics.Vector3((float)bars[i].Width / 2, (float)bars[i].Height / 2, 0f);
                visual.Scale = new System.Numerics.Vector3(1f, heights[i], 1f);
            }
        };
        _meterTimer.Start();
    }

    private void StopMeterBars()
    {
        _meterTimer?.Stop();
        _meterTimer = null;
    }
    private string? _equalizerTint = "none";

    /// <summary>
    /// Island text. <b>If a music title is wider than its slot, scroll it left</b> (user feedback 09-30: should loop seamlessly like Galaxy phones).
    /// Two copies of the title sit side by side and scroll endlessly by "one copy width + gap" — the second copy lands exactly where the first was,
    /// so jumping back to the start leaves no visible seam. Each lap pauses 2s when the first character is at the front.
    /// The animation runs on the compositor (Composition) — a Storyboard appeared to stop after one lap (user feedback 09-30).
    /// Everything else is truncated with "…". If the text is unchanged it doesn't restart (this is called every 250ms).
    /// </summary>
    private void ShowIslandText(ActivityState state, string prefix, string name)
    {
        ShowClockDigits(null);   // the timer digit slot is only for timers
        var width = AvailableTextWidth();
        ClipMarquee(width);
        var full = prefix + name;
        // Music is just playing in the background, so its title uses normal weight — only alerts (done, permission) are bold. Long all-caps titles in bold filled the slot
        // and grabbed attention (user feedback 09-30: "STUPID IN LOVE" was too loud, while the "Claude done" text weight was just right)
        _islandStrong = state.Kind != ActivityKind.Music;
        UseFontsFor(full);   // before measuring — changing the font changes the width
        var measure = IslandMeasure();

        // Long names scroll through once to show the whole thing: music titles, session names in Claude alerts.
        // (Transfers are truncated with "…" since the changing percentage would restart the scroll every time)
        var flows = state.Kind is ActivityKind.Music or ActivityKind.AgentDone or ActivityKind.AgentPermission or ActivityKind.AgentWorking or ActivityKind.Notice;
        if (flows && measure.Measure(full) > width)
        {
            _marqueePrefix = prefix;
            _marqueeName = name;
            if (_marqueeText != full)
            {
                _marqueeText = full;
                StartMarquee();   // new title: one lap
            }
            else if (!_marqueeRunning)
            {
                ShowMarqueeRest();   // when paused, show "…" (redo every time since color/width may have changed)
            }
        }
        else
        {
            StopMarquee();
            var (head, rest) = TextFit.FitParts(prefix, name, width, measure);
            SetStyledText(IslandText, head, rest);
        }

        IslandTextCopy.Foreground = IslandText.Foreground;

        // inside a canvas, so center vertically by hand
        MarqueePanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(MarqueePanel, Math.Max(0, (MarqueeHost.ActualHeight - MarqueePanel.DesiredSize.Height) / 2));
    }

    private string _marqueePrefix = string.Empty;
    private string _marqueeName = string.Empty;
    private bool _marqueeRunning;

    /// <summary>On hover, scroll the title one more lap (if it isn't already scrolling and is long enough to scroll).</summary>
    private void ReplayMarquee()
    {
        if (_marqueeText is not null && !_marqueeRunning)
        {
            StartMarquee();
        }
    }

    /// <summary>
    /// Island text is painted two ways: the key part (e.g. "42%", "Claude done", "18:42", track title) bold,
    /// the rest (e.g. "· 12s", "· session name", file name) dimmed — the same look as the desktop mockup.
    /// If the prefix contains " · ", everything before it is the key part. Without a prefix (music), the whole name is the key part.
    /// </summary>
    private void SetStyledText(Microsoft.UI.Xaml.Controls.TextBlock block, string prefix, string rest)
    {
        // "45% · 6s  " + name → bold "45%" / dimmed " · 6s · name";  "Claude done · " + name → "Claude done" / " · name"
        var head = prefix.Trim().TrimEnd('·').TrimEnd();
        string main, hint;
        if (head.Length == 0)
        {
            (main, hint) = (rest, string.Empty);   // music: the whole title is the key part
        }
        else
        {
            var dot = head.IndexOf(" · ", StringComparison.Ordinal);
            main = dot >= 0 ? head[..dot] : head;
            var parts = new List<string>();
            if (dot >= 0)
            {
                parts.Add(head[(dot + 3)..]);
            }

            if (rest.Trim().Length > 0)
            {
                parts.Add(rest.Trim());
            }

            hint = parts.Count > 0 ? " · " + string.Join(" · ", parts) : string.Empty;
        }

        block.Inlines.Clear();
        block.Inlines.Add(_islandStrong
            ? new Microsoft.UI.Xaml.Documents.Run { Text = main, FontFamily = _islandBold, FontWeight = Microsoft.UI.Text.FontWeights.Medium }
            : new Microsoft.UI.Xaml.Documents.Run { Text = main });
        if (hint.Trim().Length > 0)
        {
            block.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run
            {
                Text = hint,
                Foreground = BrushOf(_theme.Hint),
            });
        }
    }

    /// <summary>
    /// Resting look: end with "…" so text isn't chopped off abruptly at the edge of the slot (user feedback 09-30: suggested using an ellipsis).
    /// </summary>
    private void ShowMarqueeRest()
    {
        var measure = IslandMeasure();
        var (head, rest) = TextFit.FitParts(_marqueePrefix, _marqueeName, AvailableTextWidth(), measure);
        SetStyledText(IslandText, head, rest);
        IslandTextCopy.Visibility = Visibility.Collapsed;
    }

    /// <summary>One lap: place two copies of the full title side by side and scroll; when done, go back to the "…" look.</summary>
    /// <summary>
    /// Timer: "Timer 18:42" — label bold, time normal weight (like "Pomodoro 18:42" in the mockup). A named timer uses its name.
    /// When finished: "Timer done".
    /// </summary>
    private void ShowTimerText(ActivityState state, DateTimeOffset now)
    {
        StopMarquee();
        ClipMarquee(AvailableTextWidth());
        var label = string.IsNullOrWhiteSpace(state.Name) ? _text.Timer : state.Name!;
        UseFontsFor(label);
        var left = TimerParser.Elapsed(state, now) ?? TimerRemaining(state, now);   // a stopwatch counts up
        var running = TimerParser.IsStopwatch(state) || (left is { } l && l > TimeSpan.Zero);
        IslandText.Inlines.Clear();
        IslandText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = label, FontFamily = _islandBold, FontWeight = Microsoft.UI.Text.FontWeights.Medium });
        if (!running)
        {
            IslandText.Inlines.Add(new Microsoft.UI.Xaml.Documents.Run { Text = "  " + _text.Done });
        }
        ShowClockDigits(running ? FormatClock(left!.Value) : null);
        IslandText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        MarqueePanel.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Microsoft.UI.Xaml.Controls.Canvas.SetTop(MarqueePanel, Math.Max(0, (MarqueeHost.ActualHeight - MarqueePanel.DesiredSize.Height) / 2));
    }

    private string _clockShown = string.Empty;

    /// <summary>
    /// Draws the timer digits one by one. <b>Each digit slot is fixed to the widest digit</b> — drawing them as one string
    /// made the text jiggle sideways when narrow digits like "1" appeared. <b>Only changed digits</b> roll down from above
    /// (it's a countdown, so new digits come from above). null hides them.
    /// </summary>
    private void ShowClockDigits(string? clock)
    {
        if (clock is null)
        {
            ClockDigits.Visibility = Visibility.Collapsed;
            _clockShown = string.Empty;
            return;
        }

        ClockDigits.Visibility = Visibility.Visible;
        if (clock == _clockShown && ClockDigits.Children.Count == clock.Length)
        {
            return;
        }

        var rebuild = ClockDigits.Children.Count != clock.Length;
        if (rebuild)
        {
            ClockDigits.Children.Clear();
            var digitWidth = DigitWidth();
            foreach (var c in clock)
            {
                var cell = new Microsoft.UI.Xaml.Controls.TextBlock
                {
                    FontSize = IslandText.FontSize,
                    FontFamily = IslandText.FontFamily,
                    CharacterSpacing = IslandText.CharacterSpacing,
                    LineHeight = IslandText.LineHeight,
                    LineStackingStrategy = IslandText.LineStackingStrategy,
                    Foreground = IslandText.Foreground,
                    TextAlignment = TextAlignment.Center,
                };
                if (char.IsDigit(c))
                {
                    cell.Width = digitWidth;
                }

                ClockDigits.Children.Add(cell);
            }
        }

        for (var i = 0; i < clock.Length; i++)
        {
            var cell = (Microsoft.UI.Xaml.Controls.TextBlock)ClockDigits.Children[i];
            var text = clock[i].ToString();
            cell.Foreground = IslandText.Foreground;
            if (cell.Text == text)
            {
                continue;
            }

            cell.Text = text;
            if (!rebuild)
            {
                RollIn(cell);
            }
        }

        _clockShown = clock;
    }

    /// <summary>Widest digit width (current font and size).</summary>
    private double DigitWidth()
    {
        var probe = new Microsoft.UI.Xaml.Controls.TextBlock { FontSize = IslandText.FontSize, FontFamily = IslandText.FontFamily, CharacterSpacing = IslandText.CharacterSpacing };
        double widest = 0;
        for (var d = '0'; d <= '9'; d++)
        {
            probe.Text = d.ToString();
            probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
            widest = Math.Max(widest, probe.DesiredSize.Width);
        }

        return Math.Ceiling(widest);
    }

    /// <summary>One changed digit: slides down 7 from above while sharpening (0.25s).</summary>
    private static void RollIn(UIElement cell)
    {
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(cell, true);
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(cell);
        var compositor = visual.Compositor;
        var ease = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.2f, 0.8f), new System.Numerics.Vector2(0.2f, 1f));
        var move = compositor.CreateScalarKeyFrameAnimation();
        move.InsertKeyFrame(0f, -7f);
        move.InsertKeyFrame(1f, 0f, ease);
        move.Duration = TimeSpan.FromMilliseconds(250);
        visual.StartAnimation("Translation.Y", move);
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, ease);
        fade.Duration = TimeSpan.FromMilliseconds(200);
        visual.StartAnimation("Opacity", fade);
    }

    /// <summary>
    /// When to light the border: finished items (Claude/Codex done, timer finished) = green, items needing an answer (permission check) = orange.
    /// Running timers, music and transfers get no border (always-on is distracting). User feedback 09-30: timers and the like should get a border too.
    /// </summary>
    private void UpdateGlow()
    {
        var state = _snapshot.IsActive ? _snapshot.Primary : null;
        var now = DateTimeOffset.UtcNow;
        var (key, color) = state switch
        {
            _ when _dropHover => ("drop", _theme.Accent),   // dragging over — "drop here to send"
            { Kind: ActivityKind.AgentDone } => ($"done|{state.SourcePath}", _theme.Ok),
            { Kind: ActivityKind.Timer } when !state.IsPaused && TimerRemaining(state, now) <= TimeSpan.Zero
                => ($"timer|{state.SourcePath}", _theme.Ok),
            { Kind: ActivityKind.AgentPermission } => ($"ask|{state.SourcePath}", Attention()),
            // a system warning you must act on (low battery) is red — orange belongs to "an agent needs your answer" (user 10-01)
            { Kind: ActivityKind.Notice, RunState: ActivityRunState.Error } => ($"notice|{state.SourcePath}", Alarm()),
            // Music (or another non-alert item) holds the pill but a task finished → light the same thick green border so it's
            // noticeable next to the song, without hiding it (user 10-01). The subtle 1px "glow border" style stays for looks.
            _ when _backgroundDoneKey is { } background => (background, _theme.Ok),
            _ => ((string?)null, _theme.Ok),
        };
        _animator.UpdateGlow(key, color);
        _glowKey = key;
        _alerting = key is not null && !_typing;
    }

    /// <summary>The border lit now (its key) — a click acknowledges a background completion only when that is what shows.</summary>
    private string? _glowKey;


    /// <summary>Previous island content is still leaving (no redraw meanwhile).</summary>
    private bool _swapping;
    private bool _alerting;

    /// <summary>Glow key of a finished task that isn't the pill's primary (e.g. music is playing) — lights the completion border without hiding the song.</summary>
    private string? _backgroundDoneKey;

    /// <summary>The finished task behind <see cref="_backgroundDoneKey"/> (what a click on the pill acknowledges while music holds it).</summary>
    private ActivityState? _backgroundDone;

    private string? _lastMusicSig;

    /// <summary>The fields that decide how the music island looks; when unchanged tick-to-tick, the re-render is skipped.</summary>
    // The text slot's width is part of it: coming back from typing, the columns are still animating when the island first renders,
    // so the title clip was set to a mid-animation (too wide) width and — with re-renders skipped — the scrolling title ran under
    // the now-playing bars (found 10-01 right after sending a prompt).
    private string MusicRenderSig(IsleBar.Core.Island.ActivityState m)
        => string.Concat(m.SourcePath, "|", m.Name, "|", m.Msg, "|", m.Stage, "|", m.Done?.ToString(), "|", m.Total?.ToString(),
            "|", Math.Round(AvailableTextWidth()).ToString(System.Globalization.CultureInfo.InvariantCulture),
            // a task finishing behind paused music changes nothing above — its green border and the more-dot never showed (review 10-03)
            "|", _backgroundDoneKey, "|", _snapshot.ExtraCount.ToString(System.Globalization.CultureInfo.InvariantCulture));

    /// <summary>
    /// Taskbar color just left of the pill. The band outside the alert border is painted this color so the pill clip edge (can't be anti-aliased)
    /// matches the background — where green touched the clip edge it looked stair-stepped, and a white background showed a white line (user feedback 09-30).
    /// </summary>
    private DateTimeOffset _lastBesideCheck;
    private Windows.UI.Color? _besidePending;

    /// <summary>
    /// The taskbar colour around the pill was read only when the pill moved. Switching displays (laptop screen on/off) read it
    /// mid-switch as black, and it stayed: a black ring around the pill until a restart (found 10-03). So read it again every few
    /// seconds and take a new colour once two reads in a row agree (a single odd read — a full-screen video leaving — is ignored).
    /// </summary>
    private void RefreshBesideColor()
    {
        if (_typing || !_taskbar.IsAttached || DateTimeOffset.UtcNow - _lastBesideCheck < TimeSpan.FromSeconds(3))
        {
            return;
        }

        _lastBesideCheck = DateTimeOffset.UtcNow;
        if (SampleBeside() is not { } color)
        {
            return;
        }

        if (Root.Background is SolidColorBrush current && current.Color == color)
        {
            _besidePending = null;
        }
        else if (_besidePending == color)
        {
            _besidePending = null;
            Root.Background = BrushOf(color);
            TaskbarHost.Log($"taskbar colour beside the pill changed to {color}");
        }
        else
        {
            _besidePending = color;
        }
    }

    private Windows.UI.Color TaskbarColorBeside()
        => SampleBeside() ?? (ThemeColors.FromSystem().Light
            ? Windows.UI.Color.FromArgb(255, 0xDF, 0xE6, 0xF3)
            : Windows.UI.Color.FromArgb(255, 0x20, 0x20, 0x20));

    /// <summary>
    /// The screen pixel just left of the pill, or null when it can't be the taskbar: unreadable, or far from the theme's taskbar
    /// (a light taskbar read near-black while the UAC prompt dimmed the screen, and a display switch read it black — 10-03).
    /// </summary>
    private Windows.UI.Color? SampleBeside()
    {
        if (_placement.Mode == PlacementMode.Hide)
        {
            return null;
        }

        var dc = GetDC(IntPtr.Zero);
        try
        {
            var y = _placement.Y + (_height / 2);
            var c = GetPixel(dc, _placement.X - DpiSetup.Px(4), y);
            if (c == 0xFFFFFFFF)
            {
                return null;
            }

            var color = Windows.UI.Color.FromArgb(255, (byte)(c & 0xFF), (byte)((c >> 8) & 0xFF), (byte)((c >> 16) & 0xFF));
            var luma = (color.R * 0.3) + (color.G * 0.59) + (color.B * 0.11);
            // dark taskbars include a light accent colour ("show accent colour on taskbar"), so only near-white is ruled out there
            return ThemeColors.FromSystem().Light ? (luma >= 110 ? color : null) : (luma <= 210 ? color : null);
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr dc, int x, int y);

    /// <summary>Clip to the pill shape (flush, with no margin, when the alert border is on).</summary>
    private void ApplyRegion() => PillRegion.Apply(_taskbar.OuterWindow, _width, _height, flush: true);   // always flush — PillSurface draws the soft edge

    /// <summary>Orange (warning) — the Windows warning color, visible on light and dark themes.</summary>
    private static Windows.UI.Color Attention()
        => ThemeColors.FromSystem().Light
            ? Windows.UI.Color.FromArgb(255, 0xC2, 0x6A, 0x00)
            : Windows.UI.Color.FromArgb(255, 0xFC, 0xB0, 0x2F);

    /// <summary>Red (critical) — Windows' error red, for a system warning that needs action (low battery).</summary>
    private static Windows.UI.Color Alarm()
        => ThemeColors.FromSystem().Light
            ? Windows.UI.Color.FromArgb(255, 0xC4, 0x2B, 0x1C)
            : Windows.UI.Color.FromArgb(255, 0xFF, 0x5F, 0x57);   // Windows' own dark-theme critical (FF99A4) read as pink on the pill (10-01)

    private void StartMarquee()
    {
        SetStyledText(IslandText, _marqueePrefix, _marqueeName);
        SetStyledText(IslandTextCopy, _marqueePrefix, _marqueeName);
        IslandTextCopy.Visibility = Visibility.Visible;
        IslandText.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        RunMarquee(IslandText.DesiredSize.Width + MarqueeGap);
    }

    /// <summary>
    /// While scrolling, softly cover both ends of the text slot with the pill background (so text isn't chopped at the edge, like music apps).
    /// Off when resting, since it ends in "…" then.
    /// </summary>
    private void SetMarqueeFade(bool on)
    {
        FadeLeft.Visibility = FadeRight.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on || PillSurface.Background is not SolidColorBrush fill)
        {
            return;
        }

        var solid = fill.Color;
        var clear = Windows.UI.Color.FromArgb(0, solid.R, solid.G, solid.B);
        FadeLeft.Fill = Gradient(solid, clear);
        FadeRight.Fill = Gradient(clear, solid);
    }

    private static LinearGradientBrush Gradient(Windows.UI.Color from, Windows.UI.Color to)
    {
        var brush = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0, 0.5), EndPoint = new Windows.Foundation.Point(1, 0.5) };
        brush.GradientStops.Add(new GradientStop { Color = from, Offset = 0 });
        brush.GradientStops.Add(new GradientStop { Color = to, Offset = 1 });
        return brush;
    }

    private int _marqueeRun;

    private void RunMarquee(double distance)
    {
        // Number each lap — stopping an earlier lap (e.g. a scrolling music title) delivered its completion signal late
        // and stopped the new lap (a done alert) instead (measured on PC 09-30). Only react to completion of our own number.
        var myRun = ++_marqueeRun;
        _marqueeRunning = true;
        SetMarqueeFade(true);
        Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.SetIsTranslationEnabled(MarqueePanel, true);
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(MarqueePanel);
        var compositor = visual.Compositor;
        visual.StopAnimation("Translation.X");
        visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);

        // One lap = pause (first character at the front) → scroll → ends when the next copy's first character reaches the front.
        // The end and start positions look identical, so the jump back is invisible and the pause follows immediately
        // (user feedback 09-30: Galaxy scrolls and stops when the first character comes back to the front).
        var run = TimeSpan.FromSeconds(distance / MarqueeSpeed);
        var hold = _snapshot.Primary?.Kind == ActivityKind.Notice ? TimeSpan.FromSeconds(1) : MarqueeHold;   // short notices can't wait 2 s
        var total = hold + run;
        var slide = compositor.CreateScalarKeyFrameAnimation();
        slide.InsertKeyFrame(0f, 0f);
        slide.InsertKeyFrame((float)(hold / total), 0f);
        slide.InsertKeyFrame(1f, (float)-distance, compositor.CreateLinearEasingFunction());
        slide.Duration = total;
        // Only one lap — scroll once when a track first appears, then stay still once the first character is back at the front (Galaxy style,
        // user feedback 09-30: asked why it kept moving). Hovering plays one more lap (ReplayMarquee).
        slide.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Count;
        slide.IterationCount = 1;
        // Left fade only while the text moves — leaving it on while still (first 2s and at the end) covered the first character
        // and made it look clipped (user feedback 09-30: the first character was still cut off when a title first appeared).
        var fadeVisual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(FadeLeft);
        var edge = (float)Math.Min(0.4 / total.TotalSeconds, 0.05);
        var holdEnd = (float)(hold / total);
        var fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.InsertKeyFrame(0f, 0f);
        fadeIn.InsertKeyFrame(holdEnd, 0f);
        fadeIn.InsertKeyFrame(Math.Min(holdEnd + edge, 1f - edge), 1f);
        fadeIn.InsertKeyFrame(1f - edge, 1f);
        fadeIn.InsertKeyFrame(1f, 0f);
        fadeIn.Duration = total;
        var batch = compositor.CreateScopedBatch(Microsoft.UI.Composition.CompositionBatchTypes.Animation);
        visual.StartAnimation("Translation.X", slide);
        fadeVisual.StartAnimation("Opacity", fadeIn);
        batch.End();
        batch.Completed += (_, _) => _ui.TryEnqueue(() =>
        {
            if (myRun != _marqueeRun || !_marqueeRunning || _marqueeText is null)
            {
                return;
            }

            _marqueeRunning = false;
            SetMarqueeFade(false);
            visual.StopAnimation("Translation.X");
            visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
            ShowMarqueeRest();   // lap finished → "…"
        });
    }

    private void StopMarquee()
    {
        ShowClockDigits(null);
        if (_marqueeText is null)
        {
            return;
        }

        _marqueeText = null;
        _marqueeRunning = false;
        SetMarqueeFade(false);
        IslandTextCopy.Visibility = Visibility.Collapsed;
        var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(MarqueePanel);
        visual.StopAnimation("Translation.X");
        visual.Properties.InsertVector3("Translation", System.Numerics.Vector3.Zero);
    }

    private string? _albumArtKey;

    /// <summary>
    /// Album art for music in the left slot (as in the mockup). If the player provides no art, keep the note icon.
    /// Only re-read when the track changes (reading art is async).
    /// </summary>
    private async void ShowAlbumArt(bool on)
    {
        var art = on ? _media.CurrentArt : null;
        if (art is null)
        {
            AlbumArt.Visibility = Visibility.Collapsed;
            return;
        }

        LeadIcon.Visibility = Visibility.Collapsed;
        AlbumArt.Visibility = Visibility.Visible;
        var key = _media.CurrentArtKey;
        if (key == _albumArtKey)
        {
            return;
        }

        _albumArtKey = key;
        try
        {
            // XAML decodes the image later, on a worker thread, straight from the stream it was given. The player's thumbnail stream
            // can be closed before that — by leaving this method, or by the player on a track change — and decoding a closed stream
            // fail-fasts the whole bar (RO_E_CLOSED 0x80000013 in Microsoft.UI.Xaml, dump 10-01 23:33). So copy the art into a stream
            // we own and keep it alive for as long as the image may read from it.
            var owned = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            using (var source = await art.OpenReadAsync())
            {
                await Windows.Storage.Streams.RandomAccessStream.CopyAsync(source, owned);
            }

            owned.Seek(0);
            var image = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage { DecodePixelWidth = DpiSetup.Px(24) };
            await image.SetSourceAsync(owned);
            var tint = await PickAlbumTintAsync(art);
            if (_albumArtKey == key)
            {
                AlbumArtBrush.ImageSource = image;
                _albumArtStream = owned;   // the previous one is left to the GC: an image still on its way out may be reading it
                _albumTint = tint;
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)   // async void: anything escaping here ends the app (a player's cover file gone — review 10-03)
        {
            _albumArtKey = null;
            AlbumArt.Visibility = Visibility.Collapsed;
            LeadIcon.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Dominant color of the current album (null if none or grayscale → normal color).</summary>
    private Windows.UI.Color? _albumTint;

    /// <summary>Our own copy of the current album art, which the album image decodes from (see <see cref="ShowAlbumArt"/>).</summary>
    private Windows.Storage.Streams.IRandomAccessStream? _albumArtStream;

    /// <summary>Shrinks the album art to 16×16 and picks its dominant color (Core's AlbumTint). Returns null on failure.</summary>
    private async Task<Windows.UI.Color?> PickAlbumTintAsync(Windows.Storage.Streams.IRandomAccessStreamReference art)
    {
        try
        {
            using var stream = await art.OpenReadAsync();
            var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
            var pixels = await decoder.GetPixelDataAsync(
                Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                Windows.Graphics.Imaging.BitmapAlphaMode.Ignore,
                new Windows.Graphics.Imaging.BitmapTransform { ScaledWidth = 16, ScaledHeight = 16 },
                Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
                Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
            var light = PillSurface.Background is SolidColorBrush fill && ((fill.Color.R * 0.3) + (fill.Color.G * 0.59) + (fill.Color.B * 0.11)) > 128;
            return IsleBar.Core.Ui.AlbumTint.Pick(pixels.DetachPixelData(), light) is { } c
                ? Windows.UI.Color.FromArgb(255, c.R, c.G, c.B)
                : null;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)   // async void: anything escaping here ends the app (a player's cover file gone — review 10-03)
        {
            return null;
        }
    }

    /// <summary>
    /// Claude/Codex alerts (done, permission) show that agent's mark (spark / Codex mark from the user's PC) instead of ✓/🔔 —
    /// with a small ✓ next to ⌄, two symbols side by side looked flimsy (user feedback 09-30). Done/permission is signalled by the border and text color.
    /// </summary>
    private void ShowAgentMark(ActivityState state)
    {
        if (state.Kind is not (ActivityKind.AgentDone or ActivityKind.AgentPermission or ActivityKind.AgentWorking))
        {
            return;
        }

        var codex = state.AgentLabel == "Codex";
        if (codex ? CodexGlyph.Source is null : _claudeGlyph is null)
        {
            return;   // no mark on this PC → keep ✓/🔔
        }

        ClaudeGlyphImage.Source = _claudeGlyph;
        ClaudeGlyphImage.Visibility = codex ? Visibility.Collapsed : Visibility.Visible;
        CodexGlyph.Visibility = codex ? Visibility.Visible : Visibility.Collapsed;
        LeadIcon.Visibility = Visibility.Collapsed;
    }

    /// <summary>Turns the now-playing bars on/off. The animation is built once when turned on (the compositor runs it).</summary>
    private void SetEqualizer(bool on)
    {
        Equalizer.Visibility = on && !_hovering ? Visibility.Visible : Visibility.Collapsed;   // step aside when ⏮⏯⏭ appear
        var fillKey = $"{_albumTint}|{_theme.Icon}";   // repaint only when the color changes (this is called every 250ms)
        if (on && _equalizerTint != fillKey)
        {
            // the brushes are built only here — built on every call before, 4 times a second, and thrown away (review 10-03)
            Brush brush = BrushOf(_theme.Icon);
            if (_albumTint is { } tint)
            {
                var top = Windows.UI.Color.FromArgb(255, (byte)Math.Min(255, tint.R + 40), (byte)Math.Min(255, tint.G + 40), (byte)Math.Min(255, tint.B + 40));
                var gradient = new LinearGradientBrush { StartPoint = new Windows.Foundation.Point(0.5, 0), EndPoint = new Windows.Foundation.Point(0.5, 1) };
                gradient.GradientStops.Add(new GradientStop { Color = top, Offset = 0 });
                gradient.GradientStops.Add(new GradientStop { Color = tint, Offset = 1 });
                brush = gradient;
            }

            _equalizerTint = fillKey;
            Eq0.Fill = Eq1.Fill = Eq2.Fill = Eq3.Fill = brush;
        }

        // Follow the real sound (setting) — a 30 Hz timer reads the speaker meter and sets the bar heights
        var meter = on && _settings.AudioBars;
        if (meter != (_meterTimer is not null))
        {
            if (meter)
            {
                StopLoopBars();
                StartMeterBars();
            }
            else
            {
                StopMeterBars();
            }
        }

        if (meter)
        {
            return;
        }

        if (on && !_equalizerOn)
        {
            _equalizerOn = true;

            // The compositor (Composition) animates the bar sizes — a XAML Storyboard woke the UI and render threads every frame
            // and used 3–6% CPU for four bars (measured 09-30: music island 6–9% → 3% with bars hidden). Compositor animations don't wake app code.
            var patterns = new[]
            {
                (Values: new[] { 0.35f, 1.0f, 0.5f, 0.85f, 0.3f }, Seconds: 1.1),
                (Values: new[] { 0.9f, 0.4f, 1.0f, 0.55f, 0.8f }, Seconds: 0.9),
                (Values: new[] { 0.5f, 0.8f, 0.3f, 1.0f, 0.45f }, Seconds: 1.3),
                (Values: new[] { 0.75f, 0.35f, 0.9f, 0.4f, 1.0f }, Seconds: 1.0),
            };
            var bars = new[] { Eq0, Eq1, Eq2, Eq3 };
            for (var i = 0; i < bars.Length; i++)
            {
                var bar = bars[i];
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(bar);
                var compositor = visual.Compositor;
                visual.CenterPoint = new System.Numerics.Vector3((float)bar.Width / 2, (float)bar.Height / 2, 0f);   // centered pivot (09-30 G2)
                var sine = compositor.CreateCubicBezierEasingFunction(new System.Numerics.Vector2(0.37f, 0f), new System.Numerics.Vector2(0.63f, 1f));
                var animation = compositor.CreateVector3KeyFrameAnimation();
                var values = patterns[i].Values;
                for (var k = 0; k <= values.Length; k++)
                {
                    var v = values[k % values.Length];
                    animation.InsertKeyFrame((float)k / values.Length, new System.Numerics.Vector3(1f, v, 1f), sine);
                }

                animation.Duration = TimeSpan.FromSeconds(patterns[i].Seconds);
                animation.IterationBehavior = Microsoft.UI.Composition.AnimationIterationBehavior.Forever;
                animation.DelayTime = TimeSpan.FromMilliseconds(120 * i);   // stagger each bar — so they don't pump as one block
                visual.StartAnimation("Scale", animation);
            }
        }
        else if (!on && _equalizerOn)
        {
            StopLoopBars();
        }
    }
    private DateTimeOffset _lastPrune = DateTimeOffset.MinValue;

    /// <summary>
    /// If not attached to the taskbar, reattach every 2s. On startup SetParent sometimes fails with error 87,
    /// but retrying works (measured on PC 09-29 — left failed, the bar stays invisible).
    /// </summary>
    private void RetryAttachIfNeeded()
    {
        if (_taskbar.IsAttached)
        {
            _attachFailures = 0;   // attached by another path (end of typing, menu closed) — a later run of failures counts from zero
            return;
        }

        if (_typing || DateTimeOffset.UtcNow - _lastAttachTry < TimeSpan.FromSeconds(2))
        {
            return;
        }

        _lastAttachTry = DateTimeOffset.UtcNow;
        if (_taskbar.Attach())
        {
            if (_attachFailures >= AttachFailuresBeforeFallback)
            {
                TaskbarHost.Log("attach recovered — hiding the real search box again");
            }

            _attachFailures = 0;
            ApplyNativeSearchBox();   // if Explorer restarted, inject the module into the new Explorer again
            PlaceOnTaskbar();
            ApplyRegion();
        }
        else
        {
            ++_attachFailures;
            if (_attachFailures == AttachFailuresBeforeFallback && _settings.NativeSearchBox)
            {
                // Unattached, the pill sits behind the (topmost) taskbar — and with the real search box hidden the slot was just empty,
                // no search at all, until Explorer was restarted (seen 10-01). Give the real box back while we keep retrying.
                TaskbarHost.Log("attach keeps failing — showing the real search box until it works");
                _nativeShown = true;
                NativeSearchBox.Show();
            }
            else if (_attachFailures >= AttachFailuresBeforeRestart && _compose is null && !_menuOpen && RestartForAttachAllowed())
            {
                // Sometimes the taskbar keeps refusing this window (error 87) while it takes any other — a fresh start of the bar
                // attached every time (seen twice on 10-03). Once per 5 minutes at most, so a Windows-wide refusal can't loop.
                TaskbarHost.Log("attach keeps failing — restarting the bar");
                RequestExit(ExitCodes.RestartRequested);
            }
        }
    }

    private const int AttachFailuresBeforeRestart = 15;   // 30 s

    private static bool RestartForAttachAllowed()
    {
        var mark = Path.Combine(AppPaths.DataDirectory, "attach-restart.txt");
        try
        {
            if (File.Exists(mark) && DateTime.UtcNow - File.GetLastWriteTimeUtc(mark) < TimeSpan.FromMinutes(5))
            {
                return false;
            }

            File.WriteAllText(mark, DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private const int AttachFailuresBeforeFallback = 3;   // tries are 2 s apart
    private int _attachFailures;

    private void RenderIsland()
    {
        _animator.Breathe(_snapshot.IsActive && !_typing && _snapshot.Primary?.Kind == ActivityKind.AgentWorking);
        ApplyStyle();
        UpdateProgressLine();
        GlowRing.Visibility = _typing ? Visibility.Collapsed : Visibility.Visible;   // the island is hidden while typing, so the border goes too
        UpdateGlow();
        UpdateHoverButtons();
        if (RenderDropHint())
        {
            return;
        }

        // While the compose window is open the pill looks like the input box it came from — it used to snap back to an unrelated
        // status ("Claude working · …") under the window you were typing in (design check 10-01)
        if (!_snapshot.IsActive || _typing || _compose is not null)
        {
            SetEqualizer(false);
            ShowAlbumArt(false);
            StopMarquee();
            IslandContent.Visibility = Visibility.Collapsed;
            Entry.Visibility = Visibility.Visible;
            MoreDot.Visibility = Visibility.Collapsed;
            HoverActions.Visibility = Visibility.Collapsed;
            UpdateLeadIcon();
            UpdateChevron();   // place the "next" slot with the album off (the spark appeared in the album spot and then visibly moved — user feedback 09-30)
            return;
        }

        var state = _snapshot.Primary!;
        RenderIslandState(state);
    }

    /// <summary>
    /// While a file is dragged over the pill, show it there instead of only the blue border (user 10-01): with a drop command set,
    /// just a file icon and the file's name (whatever the command does, you're handing it a file — so no wording that would be wrong
    /// for someone else's command); with none, "Attach to prompt · name". Returns false when not dragging.
    /// </summary>
    private bool RenderDropHint()
    {
        if (!_dropHover || _typing)
        {
            return false;
        }

        var hasCommand = !string.IsNullOrWhiteSpace(_settings.DropCommand);
        RenderIslandState(new ActivityState
        {
            RawKind = ActivityState.KindNotice,
            Title = hasCommand ? _dropNames ?? string.Empty : _text.DropToAttach,
            Name = hasCommand ? null : _dropNames,
            Glyph = hasCommand ? "\uE8A5" : "\uE723",   // document / attach 📎
        });
        return true;
    }

    private void RenderIslandState(ActivityState state)
    {
        Entry.Visibility = Visibility.Collapsed;
        IslandContent.Visibility = Visibility.Visible;
        MoreDot.Visibility = _snapshot.HasMore ? Visibility.Visible : Visibility.Collapsed;

        UpdateLeadIcon();
        LeadIcon.Glyph = state.IsPaused ? "\uE769" : GlyphFor(state);   // paused timer = ⏸
        ShowAgentMark(state);
        ShowAlbumArt(state.Kind == ActivityKind.Music);
        SetEqualizer(state.Kind == ActivityKind.Music && state.Stage != "paused");
        UpdateChevron();   // place the "next" slot with the album on — placing it first made the album appear in the icon spot and then get pushed aside (user feedback 09-30)
        var now = DateTimeOffset.UtcNow;
        // The done-alert hook only carries the session name (no folder name — user feedback 09-30). Without a name, just "Claude done"
        var name = state.Name;
        if (state.Kind == ActivityKind.Timer)
        {
            ShowTimerText(state, now);
        }
        else if (state.Kind == ActivityKind.Notice)
        {
            // brief notice: "Charging · 78%" — title is the key part (bold), description dimmed
            var tail = string.Join(" · ", new[] { state.Msg, state.Name }.Where(t => !string.IsNullOrWhiteSpace(t)));
            ShowIslandText(state, (state.Title ?? string.Empty) + (tail.Length == 0 ? string.Empty : " · "), tail);
        }
        else if (state.Kind == ActivityKind.Music)
        {
            // music: title is the key part (bold), artist dimmed — "title · artist"
            ShowIslandText(state, state.Msg is null ? string.Empty : (name ?? string.Empty) + " · ", state.Msg ?? name ?? string.Empty);
        }
        else
        {
            ShowIslandText(state, PrefixFor(state, now), name ?? string.Empty);
        }
        IslandText.Foreground = BrushOf(ForegroundFor(state, now));

    }

    /// <summary>
    /// Progress bar. Separate from the island text — keeps showing the most urgent item's progress even while typing.
    /// Collapsed for items without progress (music, notices) or when the island is empty.
    /// </summary>
    /// <summary>Timer ring: an arc of <paramref name="fraction"/> clockwise from 12 o'clock (stroke 2, inside a 16×16 box).</summary>
    private void DrawTimerArc(double fraction)
    {
        const double center = 8, radius = 7;   // the middle of a 2-thick stroke sits 1 in from the outside
        fraction = Math.Clamp(fraction, 0, 1);
        if (fraction <= 0.001)
        {
            TimerArc.Data = null;
            return;
        }

        if (fraction >= 0.999)
        {
            TimerArc.Data = new EllipseGeometry { Center = new Windows.Foundation.Point(center, center), RadiusX = radius, RadiusY = radius };
            return;
        }

        var angle = fraction * 2 * Math.PI;
        var end = new Windows.Foundation.Point(center + (radius * Math.Sin(angle)), center - (radius * Math.Cos(angle)));
        var figure = new PathFigure { StartPoint = new Windows.Foundation.Point(center, center - radius), IsClosed = false };
        figure.Segments.Add(new ArcSegment
        {
            Point = end,
            Size = new Windows.Foundation.Size(radius, radius),
            IsLargeArc = fraction > 0.5,
            SweepDirection = SweepDirection.Clockwise,
        });
        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        TimerArc.Data = geometry;
    }

    private void UpdateProgressLine()
    {
        if (_snapshot.Primary is not { } state)
        {
            ProgressLine.Width = 0;
            TimerRing.Visibility = Visibility.Collapsed;
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var fraction = state.Kind == ActivityKind.Timer ? TimerFraction(state, now) : state.Fraction;

        // timers use a circular gauge on the right of the island (as in the mockup, user feedback 09-30); when typing hides the island, the bottom bar
        var ring = state.Kind == ActivityKind.Timer && !_typing && !_hovering && fraction is not null
                   && TimerRemaining(state, now) > TimeSpan.Zero;   // when finished, a green border instead of the ring
        TimerRing.Visibility = ring ? Visibility.Visible : Visibility.Collapsed;
        if (ring)
        {
            DrawTimerArc(fraction!.Value);
            TimerArc.Stroke = BrushOf(state.IsPaused ? _theme.Hint : _theme.Accent);
            // gray background ring (so the remaining part is visible, as in the mockup)
            TimerTrack.Stroke = BrushOf(Windows.UI.Color.FromArgb(0x40, _theme.Hint.R, _theme.Hint.G, _theme.Hint.B));
            ProgressLine.Width = 0;
            return;
        }
        ProgressLine.Background = BrushOf(state.RunState switch
        {
            ActivityRunState.Done => _theme.Ok,
            ActivityRunState.Error => _theme.Bad,
            _ when state.Kind == ActivityKind.Music && _albumTint is { } tint => tint,   // music uses the album color (09-30 design G2)
            _ when state.Kind == ActivityKind.Timer && TimerRemaining(state, DateTimeOffset.UtcNow) <= TimeSpan.Zero => _theme.Ok,   // a finished timer is green, like the border
            _ => _theme.Accent,
        });
        var track = ProgressTrackWidth();
        if (state.Kind == ActivityKind.Transfer && state.RunState == ActivityRunState.Running
            && (state.IsIndeterminate || state.Stage == "download"))
        {
            // if the fraction is unknown (preparing/finishing), a quarter-length segment flows left to right (same as the Python version)
            var piece = track / 4;
            var phase = now.ToUnixTimeMilliseconds() % 1600 / 1600.0;
            var x = (phase * (track + piece)) - piece;
            var left = Math.Max(0, x);
            ProgressLine.Margin = new Thickness(ProgressTrackLeft() + left, 0, 0, 3);
            ProgressLine.Width = Math.Max(1, Math.Min(track, x + piece) - left);
            return;
        }

        ProgressLine.Margin = new Thickness(ProgressTrackLeft(), 0, 0, 3);
        // progress is based on the amount actually transferred
        ProgressLine.Width = fraction is { } f ? Math.Max(1, track * f) : 0;
    }

    /// <summary>Prefix text. Differs by kind, and its length varies by language and progress, so it's included when truncating the name.</summary>
    private string PrefixFor(ActivityState state, DateTimeOffset now) => state.Kind switch
    {
        ActivityKind.Timer => TimerRemaining(state, now) is { } left && left > TimeSpan.Zero
            ? $"{FormatClock(left)}  "   // paused shows as the left ⏸ icon + dimmed text (no emoji, it renders as a blue box)
            : $"{_text.Done}  ",
        ActivityKind.Music => "",   // paused shows as the left ⏸ icon + dimmed text
        ActivityKind.AgentDone => string.IsNullOrEmpty(state.Name)
            ? $"{state.AgentLabel} {_text.AgentDone}"
            : $"{state.AgentLabel} {_text.AgentDone} · ",   // no emoji, to avoid clashing with the left ✓ icon
        // "Claude needs you · name" — the orange border alone carried the meaning since the left icon became the agent mark
        // (the 🔔 used to say it); working and done both name their state, so this one does too (design check 10-01)
        ActivityKind.AgentPermission => string.IsNullOrEmpty(state.Name)
            ? $"{state.AgentLabel} {_text.NeedsAnswer}"
            : $"{state.AgentLabel} {_text.NeedsAnswer} · ",
        ActivityKind.AgentWorking => string.IsNullOrEmpty(state.Name)
            ? $"{state.AgentLabel} {_text.Working}"
            : $"{state.AgentLabel} {_text.Working} · ",
        _ => state.RunState switch
        {
            ActivityRunState.Done => $"{_text.Done}  ",
            ActivityRunState.Error => $"{_text.Fail}  ",
            _ when state.Stage == "download" && state.Done is { } got => $"{FormatBytes(got)}  ",   // browser download: amount received
            _ when state.IsIndeterminate => $"{(state.IsFinishing ? _text.Finishing : _text.Prep)}  ",
            _ => $"{state.Fraction * 100:0}%{TransferEta(state, now)}  ",
        },
    };

    /// <summary>"820 KB" · "12.3 MB" · "1.24 GB".</summary>
    private static string FormatBytes(long bytes) => bytes switch
    {
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:0.0} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:0.00} GB",
    };

    private static TimeSpan? TimerRemaining(ActivityState state, DateTimeOffset now) => TimerParser.Remaining(state, now);

    private string? _speedPath;
    private readonly List<(DateTimeOffset At, long Done)> _speedSamples = [];

    /// <summary>
    /// Transfer remaining time " · 1m 20s". Speed is measured from the amount moved in the last 5 seconds (same as the Python version).
    /// Empty text if the speed isn't known yet.
    /// </summary>
    private string TransferEta(ActivityState state, DateTimeOffset now)
    {
        if (state.Done is not { } done || state.Total is not > 0)
        {
            return string.Empty;
        }

        if (state.SourcePath != _speedPath)
        {
            _speedPath = state.SourcePath;
            _speedSamples.Clear();
        }

        if (_speedSamples.Count == 0 || _speedSamples[^1].Done != done || now - _speedSamples[^1].At > TimeSpan.FromSeconds(1))
        {
            _speedSamples.Add((now, done));
        }

        _speedSamples.RemoveAll(sample => now - sample.At > TimeSpan.FromSeconds(5));
        if (_speedSamples.Count < 2)
        {
            return string.Empty;
        }

        var seconds = (_speedSamples[^1].At - _speedSamples[0].At).TotalSeconds;
        var speed = seconds > 0 ? (_speedSamples[^1].Done - _speedSamples[0].Done) / seconds : 0;
        return speed > 0 ? $" · {_text.FormatDuration((state.Total.Value - done) / speed)}" : string.Empty;
    }

    /// <summary>Timer bar: fraction of time elapsed (stops in place when paused).</summary>
    private static double? TimerFraction(ActivityState state, DateTimeOffset now) => TimerParser.Fraction(state, now);

    /// <summary>Remaining time "12:34" or "1:02:03".</summary>
    private static string FormatClock(TimeSpan left)
    {
        var total = (int)Math.Ceiling(left.TotalSeconds);
        return total >= 3600
            ? $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}"
            : $"{total / 60}:{total % 60:00}";
    }

    private static string DirectionGlyph(ActivityState state)
        => state.Direction == TransferDirection.Incoming ? "⬇" : "⬆";

    /// <summary>Left icon. The text uses no emoji (blue boxes, overlaps), so the icon conveys kind, direction and result.</summary>
    private static string GlyphFor(ActivityState state) => state.Kind switch
    {
        ActivityKind.AgentPermission => "\uEA8F",                       // bell
        ActivityKind.Error => "\uEA39",                                 // error
        ActivityKind.Transfer => state.RunState switch
        {
            ActivityRunState.Done => "\uE73E",                          // ✓
            ActivityRunState.Error => "\uEA39",
            _ => state.Direction == TransferDirection.Incoming ? "\uE896" : "\uE898",   // receive ↓ / send ↑
        },
        ActivityKind.AgentDone => "\uE73E",
        ActivityKind.Timer => "\uE916",                                 // stopwatch
        ActivityKind.Notice => state.Glyph ?? "\uE7E7",                   // icon chosen by the notice (bell if none)
        ActivityKind.Music => state.Stage == "paused" ? "\uE769" : "\uE8D6",
        _ => "\uE721",
    };

    private Windows.UI.Color ForegroundFor(ActivityState state, DateTimeOffset now)
        => state.IsPaused || (state.Kind == ActivityKind.Music && state.Stage == "paused") ? _theme.Hint
            : state.Kind == ActivityKind.Timer && TimerRemaining(state, now) <= TimeSpan.Zero ? _theme.Ok
            : state.Kind == ActivityKind.AgentDone ? _theme.Ok
            : state.Kind == ActivityKind.Notice ? _theme.Foreground
            : ForegroundFor(state);

    private Windows.UI.Color ForegroundFor(ActivityState state) => state.RunState switch
    {
        ActivityRunState.Done => _theme.Ok,
        ActivityRunState.Error => _theme.Bad,
        _ => _theme.Foreground,
    };

    /// <summary>
    /// Width available for island text and bar (<b>DIP</b> — WinUI units). Physical pixels (_width) used to be passed as-is,
    /// so on a 200% display the bar filled over twice as fast and was already full with 10s left (user feedback 09-29).
    /// Before layout (first show), the default width minus the left ⌄/icon and the right margin.
    /// </summary>
    private double IslandTrackWidth()
        => PillBody.ColumnDefinitions[2].ActualWidth > 0 ? PillBody.ColumnDefinitions[2].ActualWidth : BaseWidth - 70;   // middle column (same width while typing)

    private double AvailableTextWidth() => IslandTrackWidth() - 4;

    /// <summary>
    /// Progress bar position (start and length relative to the whole pill). <b>Remembered while showing the island and reused while typing</b> —
    /// typing mode has a different left column width, so the bar's start and length appeared to shift (user feedback 09-30: fine if it goes under the icon, just make it consistent).
    /// </summary>
    private (double Left, double Width) ProgressTrack()
    {
        // Remember the track only in the plain music view — NOT while hovering (the ⏮⏯⏭ buttons appear and shrink the columns)
        // and NOT while typing, AND only once the middle column width has been the same for two ticks. During the switch between
        // the search box and the island the columns animate, so a mid-animation width was captured and the bar's length jumped
        // around as you switched back and forth (user 09-30: "the play bar shrinks then grows"). Capturing a settled width fixes it.
        var mid = PillBody.ColumnDefinitions[2].ActualWidth;
        if (!_typing && !_hovering && mid > 0 && Math.Abs(mid - _lastMidWidth) < 0.5)
        {
            _progressTrack = (PillBody.ColumnDefinitions[0].ActualWidth + PillBody.ColumnDefinitions[1].ActualWidth, IslandTrackWidth());
        }

        _lastMidWidth = mid;
        return _progressTrack ?? (PillBody.ColumnDefinitions[0].ActualWidth + PillBody.ColumnDefinitions[1].ActualWidth, IslandTrackWidth());
    }

    private (double Left, double Width)? _progressTrack;
    private double _lastMidWidth = -1;

    private double ProgressTrackWidth() => ProgressTrack().Width;

    private double ProgressTrackLeft() => ProgressTrack().Left;

    // ---------------- Appearance ----------------

    /// <summary>Font for the bold part of the island text (track title, "42%", etc.) — set by UseFontsFor.</summary>
    private FontFamily _islandBold = new("Segoe UI");

    // one brush per colour, shared: the pill set fresh brushes on its text, ring and lines on every draw (4 a second), and
    // their native halves were freed only when a rare GC ran — memory crept up all day (review 10-03). Nothing changes a
    // brush's colour after it is set, so sharing is safe.
    private readonly Dictionary<Windows.UI.Color, SolidColorBrush> _brushes = [];

    private SolidColorBrush BrushOf(Windows.UI.Color color)
    {
        if (!_brushes.TryGetValue(color, out var brush))
        {
            if (_brushes.Count > 128)
            {
                _brushes.Clear();   // album tints and blended colours come and go
            }

            _brushes[color] = brush = new SolidColorBrush(color);
        }

        return brush;
    }

    /// <summary>Clips the text area to <paramref name="width"/> — reusing the clip (a new geometry on every draw before).</summary>
    private void ClipMarquee(double width)
    {
        var rect = new Windows.Foundation.Rect(0, 0, Math.Max(0, width), Math.Max(1, MarqueeHost.ActualHeight));
        if (MarqueeHost.Clip is RectangleGeometry clip)
        {
            if (clip.Rect != rect)
            {
                clip.Rect = rect;
            }
        }
        else
        {
            MarqueeHost.Clip = new RectangleGeometry { Rect = rect };
        }
    }

    private TextBlockMeasure? _measure;
    private FontFamily? _measureBold;

    /// <summary>The pill text's measurer, kept while the bold font stays the same (one per draw before — review 10-03).</summary>
    private TextBlockMeasure IslandMeasure()
    {
        var bold = _islandStrong ? _islandBold : null;
        if (_measure is null || !ReferenceEquals(_measureBold, bold))
        {
            (_measure, _measureBold) = (new TextBlockMeasure(IslandText, bold), bold);
        }

        return _measure;
    }

    /// <summary>Whether to bold the key part of the island text (alerts, progress) — music uses normal weight.</summary>
    private bool _islandStrong = true;

    /// <summary>Island text font, matched to the characters in the text (Latin, Hangul, kana) — UiFonts.Stack.</summary>
    private void UseFontsFor(string text)
    {
        (var family, _islandBold) = AppFonts.For(text, _language);
        if (!ReferenceEquals(IslandText.FontFamily, family))
        {
            IslandText.FontFamily = IslandTextCopy.FontFamily = family;   // WinUI's Grid has no FontFamily — set it on the text elements directly
        }
    }

    private void ApplyFonts()
    {
        UseFontsFor(string.Empty);
        // the input box uses Pretendard too (ImeFont handles the IME composition text separately)
        Entry.FontFamily = AppFonts.For(null, _language).Normal;
        if (_results is not null)
        {
            AppFonts.Apply(_results, _language);
        }

        if (_preview is not null)
        {
            AppFonts.Apply(_preview, _language);   // Korean / Japanese / Chinese glyph shapes differ (review 10-03)
        }

        Entry.PlaceholderText = Placeholders.For(_mode, _settings.Agent);
    }

    /// <summary>
    /// Applies colors. <b>Text color follows the actual background brightness, not the setting</b> —
    /// during a theme switch the registry and the screen disagree, and trusting the setting makes text vanish.
    /// </summary>
    /// <summary>
    /// The Claude spark (tinted with the theme's icon colour) and the Codex mark (black or white variant), both read from
    /// the user's own PC — nothing of Anthropic's or OpenAI's is shipped (user 10-01). Called again when the theme changes.
    /// </summary>
    private void LoadAgentMarks()
    {
        _claudeGlyph = ClaudeGlyph.Create(
            ClaudeExecutable.Resolve(AgentProfiles.Get(AgentKind.Claude)), DpiSetup.Px(21), _theme.Icon);
        CodexGlyph.Source = CodexMark.Find(CodexMark.DefaultRoots(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)),
                ThemeColors.FromSystem().Light) is { } blossom
            ? new Microsoft.UI.Xaml.Media.Imaging.SvgImageSource(new Uri(blossom))
            : null;
    }

    /// <summary>
    /// Notice settings for the watchers. The Store build leaves updates to the Store (its policy forbids self-updating) and
    /// doesn't offer hiding Windows' banners (it has no uninstaller that could give them back).
    /// </summary>
    private IsleBar.Core.SystemWatch.SystemWatchOptions WatchOptions()
    {
        var options = IsleBar.Core.SystemWatch.SystemWatchOptions.From(_settings);
        return AppPaths.IsPackaged ? options with { Updates = false, HideBanners = false } : options;
    }

    /// <summary>
    /// Store build, first start: there was no setup program to connect Claude Code / Codex, so offer it once with a card
    /// (clicking it connects; settings has the same switch).
    /// </summary>
    private void OfferToConnectAgents()
    {
        var flag = Path.Combine(AppPaths.DataDirectory, "connect_offered.txt");
        if (!AppPaths.IsPackaged || HookConnector.IsConnected || File.Exists(flag))
        {
            return;
        }

        try
        {
            File.WriteAllText(flag, "1");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }

        var notice = IsleBar.Core.SystemWatch.SystemNotice.Make("Claude Code · Codex", "", DateTimeOffset.UtcNow,
            msg: _text.ConnectAgentsCard, open: HookConnector.Command);
        notice.Stage = "expand";
        notice.Name = "IsleBar";
        _activities.Write("islebar_connect", notice);
    }

    /// <summary>
    /// Light/dark switched while running: recolour in place. The taskbar itself often repaints a few seconds after the
    /// setting changes, so the colours sampled from it are taken again a few times while it settles.
    /// </summary>
    private void Retheme()
    {
        ApplyTheme();
        LoadAgentMarks();
        ApplyStyle();
        SetMarqueeFade(_marqueeRunning);   // the edge fades of a scrolling title keep the old pill colour otherwise (grey boxes)
        UpdateLeadIcon();
        RenderIsland();
        foreach (var delay in new[] { 0.8, 2.0, 4.0, 7.0 })
        {
            _ = Task.Delay(TimeSpan.FromSeconds(delay)).ContinueWith(_ => _ui.TryEnqueue(() =>
            {
                ApplyTheme();
                ApplyStyle();
                SetMarqueeFade(_marqueeRunning);
                RenderIsland();
            }), TaskScheduler.Default);
        }
    }

    private void ApplyTheme()
    {
        var background = RealSearchBoxFill();
        _theme = ThemeColors.ForActualBackground(background, ThemeColors.AccentColor());
        PillSurface.Background = BrushOf(background);
        Root.Background = BrushOf(TaskbarColorBeside());   // outside the pill (the stair-steps of the clip edge sit on this color and become invisible)
        LeadIcon.Foreground = BrushOf(_theme.Icon);
        Entry.Foreground = BrushOf(_theme.Foreground);
        MoreDot.Fill = BrushOf(_theme.Hint);
        PaintChevron();
        PaintFill();
    }

    /// <summary>
    /// The real search box background color. To decide in phase 0: sample it from a screen capture, or
    /// use theme defaults (light: F2F6FC / dark: white at 6% alpha).
    /// The Python version captured behind the window and used the median of the text-free top row.
    /// </summary>
    private Windows.UI.Color RealSearchBoxFill()
        => ThemeColors.FromSystem().Light
            ? Windows.UI.Color.FromArgb(255, 242, 246, 252)
            : Windows.UI.Color.FromArgb(255, 44, 44, 44);

    private bool _hovering;


    /// <summary>
    /// Apply preferences: re-apply colors the moment the black background toggles (text color follows actual background brightness);
    /// the glow border uses the album color for music, otherwise a faint color matching the background. Safe to call on every draw.
    /// </summary>
    private void ApplyStyle()
    {
        var glass = _settings.StyleGlass && !_typing;
        GlassEdge.Visibility = glass ? Visibility.Visible : Visibility.Collapsed;
        if (!glass)
        {
            return;
        }

        var dark = !(PillSurface.Background is SolidColorBrush fill && ((fill.Color.R * 0.3) + (fill.Color.G * 0.59) + (fill.Color.B * 0.11)) > 128);
        var edge = _snapshot.IsActive && _snapshot.Primary?.Kind == ActivityKind.Music && _albumTint is { } tint
            ? Windows.UI.Color.FromArgb(140, tint.R, tint.G, tint.B)
            : dark ? Windows.UI.Color.FromArgb(26, 255, 255, 255) : Windows.UI.Color.FromArgb(22, 0, 0, 0);
        GlassBorder.BorderBrush = BrushOf(edge);
        GlassShine.Visibility = dark ? Visibility.Visible : Visibility.Collapsed;   // the white shine line isn't visible on light backgrounds
    }

    /// <summary>Pill background: normal / hovered or typing (slightly brighter — same response as the real search box, Python version's LOOK).</summary>
    private void PaintFill()
    {
        var rest = RealSearchBoxFill();
        var lift = _hovering || _typing;
        var light = ThemeColors.FromSystem().Light;
        PillSurface.Background = BrushOf(!lift ? rest
            : light ? Blend(rest, Windows.UI.Color.FromArgb(255, 255, 255, 255), 0.55)
            : Blend(rest, Windows.UI.Color.FromArgb(255, 255, 255, 255), 0.05));
        PaintEdge();
    }

    /// <summary>Thin line around the pill edge (like the real search box border) — a faint line matched to background brightness.</summary>
    private void PaintEdge()
    {
        var light = PillSurface.Background is SolidColorBrush fill && ((fill.Color.R * 0.3) + (fill.Color.G * 0.59) + (fill.Color.B * 0.11)) > 128;
        PillSurface.BorderBrush = BrushOf(light
            ? Windows.UI.Color.FromArgb(34, 0, 0, 40)
            : Windows.UI.Color.FromArgb(30, 255, 255, 255));
    }

    private static Windows.UI.Color Blend(Windows.UI.Color a, Windows.UI.Color b, double t)
        => Windows.UI.Color.FromArgb(255,
            (byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));

    /// <summary>
    /// The pill only hears "pointer left" from XAML, and as a child of the taskbar that event can be missed (a fast move off the
    /// pill left ⏮⏯⏭ showing with the mouse elsewhere — in-use test 10-01). So on each poll, if we think we're hovered but the
    /// cursor is outside the pill's window, end the hover.
    /// </summary>
    private void EndStaleHover()
    {
        if (!_hovering
            || !Interop.NativeMethods.GetCursorPos(out var cursor)
            || !Interop.NativeMethods.GetWindowRect(_taskbar.OuterWindow, out var pill))
        {
            return;
        }

        if (cursor.X < pill.Left || cursor.X >= pill.Right || cursor.Y < pill.Top || cursor.Y >= pill.Bottom)
        {
            SetHover(false);
        }
    }

    private void SetHover(bool hovering)
    {
        if (hovering && !_hovering)
        {
            ReplayMarquee();
        }

        _hovering = hovering;
        PaintFill();
        SetMarqueeFade(_marqueeRunning);
        UpdateHoverButtons();   // show/hide the ⏮⏯⏭ buttons FIRST so the text slot's width is already correct...
        if (!_typing)
        {
            Root.UpdateLayout();   // ...then re-measure and redraw the title at that width in the same pass, so it never
            RenderIsland();        // briefly overlaps the buttons (the old order drew the title full-width, then the buttons appeared over it — user 09-30)
        }
    }

    /// <summary>
    /// On hover, buttons appear <b>inside</b> the search box (no separate window).
    /// Music = ⏮⏯⏭ · timer = ⏸/▶ + ✕ · finished alerts/transfers = ✕.
    /// </summary>
    /// <summary>
    /// ⌄ is hidden while an island shows and fades in on hover — right next to the album art it looked cramped (user feedback 09-30).
    /// Always visible normally and while typing. Its slot stays (opacity only), so hovering doesn't shift the text.
    /// </summary>
    private void UpdateChevron()
    {
        // While an island shows, drop ⌄ entirely — just hiding it left an empty gap (user feedback 09-30), and showing it on hover
        // pushes the album art around. Launch options are reached via the ⌄ that appears when you click the search box to type.
        // ⌄ is always visible (user feedback 09-30: with matched spacing it's fine next to the album — consistency). Clicking it opens settings even with an island.
        var show = !_alerting;   // hide ⌄ during alerts (when the border lights up) — next to the agent mark it looks bad (user feedback 09-30)
        ChevronButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        // When ⌄ is visible, every mode uses the same "pill edge – ⌄ – icon (album)" spacing (user feedback 09-30: align everything to ⌄).
        // Without ⌄ (music and other islands), the album sits 8 from the pill edge (user: just right as is) — measured from the slot center.
        // The icon (17) aligns its left edge with the album (24), not its center — centering made the gap to ⌄ look loose (user feedback 09-30).
        var slot = show ? ChevronThenArt() : new Thickness(8 + ArtOverhang, 0, LeadGap, 0);
        // Icons (spark, folder, etc.) are about album-sized and in the same spot — when icon and text positions varied by mode
        // it lacked consistency (user feedback 09-30: wondered whether the icons needed trimming). So the slot and text start match the album case.

        LeadSlot.Margin = slot;
    }

    /// <summary>
    /// For music with ⌄ visible (while settings are open), make the "pill edge – ⌄" and "⌄ – album art" gaps equal
    /// (user feedback 09-30: ⌄ and the album looked stuck together). The ⌄ glyph's actual position is measured via the button's padding.
    /// </summary>
    /// <summary>How far the album art (24) overflows the left slot (17) on each side — slot center and album center coincide.</summary>
    private const double ArtOverhang = 3.5;

    /// <summary>From the icon's (album's) right edge to the text.</summary>
    private const double LeadGap = 9 + ArtOverhang;

    /// <summary>
    /// Album (icon) position when ⌄ is present. The album's left edge is fixed at 18 from the window edge (user feedback 09-30: that position is just right) —
    /// ⌄ sits centered between the pill edge and the album via XAML margins (measured from capture: ⌄ glyph width 7, gap 5 on each side).
    /// </summary>
    private Thickness ChevronThenArt()
    {
        const double artLeft = 18;
        var column0 = ChevronButton.Margin.Left + (ChevronButton.ActualWidth > 0 ? ChevronButton.ActualWidth : 17);
        return new Thickness(Math.Max(0, artLeft + ArtOverhang - column0), 0, LeadGap, 0);
    }

    private void UpdateHoverButtons()
    {
        UpdateChevron();
        var state = _snapshot.Primary;
        var show = _hovering && !_typing && state is not null;
        HoverActions.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        var music = state!.Kind == ActivityKind.Music;
        if (music)
        {
            Equalizer.Visibility = Visibility.Collapsed;   // move the bars out of the way the instant the buttons appear (they overlapped until the next draw)
        }

        var timer = state.Kind == ActivityKind.Timer;
        var removable = !music && state.SourcePath is { } path && File.Exists(path);
        PrevButton.Visibility = NextButton.Visibility = PlayButton.Visibility = music ? Visibility.Visible : Visibility.Collapsed;
        // a stopwatch has no time left (Remaining is null) but pauses too — its button never showed (review 10-03)
        PauseButton.Visibility = timer && (TimerParser.IsStopwatch(state) || TimerParser.Remaining(state, DateTimeOffset.UtcNow) > TimeSpan.Zero)
            ? Visibility.Visible
            : Visibility.Collapsed;
        PauseButton.Content = state.IsPaused ? "\uE768" : "\uE769";   // ▶ resume / ⏸ pause
        DismissButton.Visibility = removable ? Visibility.Visible : Visibility.Collapsed;
        if (music)
        {
            PlayButton.Content = state.Stage == "paused" ? "\uE768" : "\uE769";
        }
    }

    /// <summary>Timer pause ↔ resume. Rewrites the state file (so other windows/commands see the same state).</summary>
    private void ToggleTimerPause()
    {
        if (_snapshot.Primary is not { Kind: ActivityKind.Timer, SourcePath: { } path } state)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var next = state.IsPaused ? TimerParser.Resume(state, now) : TimerParser.Pause(state, now);
        try
        {
            _activities.Write(Path.GetFileName(path), next);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }

        UpdateHoverButtons();
        RenderIsland();
    }

    /// <summary>Removes the currently shown item (cancel a timer, clear a finished alert).</summary>
    private void DismissPrimary()
    {
        if (_snapshot.Primary?.SourcePath is not { } path)
        {
            return;
        }

        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    // ---------------- Not implemented yet (to fill in after phase 0) ----------------

    private enum MediaCommand
    {
        PlayPause,
        Previous,
        Next,
    }

    /// <summary>TODO(phase 2): send via GlobalSystemMediaTransportControlsSessionManager.</summary>
    private void SendMediaCommand(MediaCommand command)
        => _ = _media.SendAsync(command switch
        {
            MediaCommand.PlayPause => MediaAction.PlayPause,
            MediaCommand.Previous => MediaAction.Previous,
            _ => MediaAction.Next,
        });

    /// <summary>⌄ quick launch options window (pinned options only). Closes it if already open.</summary>
    private void ShowQuickOptions()
    {
        if (_flyout is not null)
        {
            _flyout.Close();
            return;
        }

        // Pressing ⌄ while the window is open first deactivates it (it closes on click-away), so by the time this click runs it's
        // already gone and used to open again at once — ⌄ could never close it (user 10-01). A close this instant was that click.
        if (DateTimeOffset.UtcNow - _flyoutClosedAt < TimeSpan.FromMilliseconds(400))
        {
            return;
        }

        _flyout = new Options.OptionsFlyout(_settings, _text, OnSettingsChanged, ShowSettings);
        _flyout.Closed += (_, _) =>
        {
            _flyout = null;
            _flyoutClosedAt = DateTimeOffset.UtcNow;
        };
        AppFonts.Apply(_flyout, _language);   // before measuring size
        _flyout.ShowAbove(PopupAnchorX, TaskbarTop());
    }

    private DateTimeOffset _flyoutClosedAt = DateTimeOffset.MinValue;

    /// <summary>
    /// Horizontal centre of the pill: windows that open above it (options, settings, compose, preview, file results) are centred
    /// on it — they used to start at its left edge and hang off to the right (user 10-01).
    /// </summary>
    private int PopupAnchorX => Spot.X + (_width / 2);

    /// <summary>
    /// Where the pill is — or, while it's hidden for lack of room (search box off, icons left-aligned), the middle of the screen just
    /// above the taskbar: typing with the hotkey and message cards used (0,0) then, the top-left corner or off screen (review 10-03).
    /// </summary>
    private (int X, int Y) Spot
    {
        get
        {
            if (_placement.Mode != PlacementMode.Hide)
            {
                return (_placement.X, _placement.Y);
            }

            var screen = Microsoft.UI.Windowing.DisplayArea.Primary.OuterBounds;
            return (screen.X + ((screen.Width - _width) / 2), TaskbarTop() - _height - DpiSetup.Px(12));
        }
    }

    /// <summary>Puts the attached pill at its spot on the taskbar, or keeps it hidden when there is none.</summary>
    private void PlaceOnTaskbar()
    {
        if (_placement.Mode == PlacementMode.Hide)
        {
            Interop.NativeMethods.ShowWindow(_taskbar.OuterWindow, Interop.NativeMethods.SW_HIDE);
            return;
        }

        _taskbar.PlaceAt(_placement.X, _placement.Y, _width, _height);
    }

    /// <summary>Settings window. Brings it to the front if already open.</summary>
    private void ShowSettings()
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new Options.SettingsWindow(_settings, _text, OnSettingsChanged, OnLanguageChanged, () => _language);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.ShowAbove(PopupAnchorX, TaskbarTop());
    }

    /// <summary>Every time a value changes in the options or settings window: save and apply it to the bar.</summary>
    /// <summary>If any launch option differs from its default, tint ⌄ with the accent color (same as the Python version's paint_chev).</summary>
    private void PaintChevron()
    {
        var now = _settings.ValuesFor(_settings.Agent);
        var plain = new LaunchOptions();
        var changed = now.Session != plain.Session || now.Model != plain.Model || now.Effort != plain.Effort
                      || now.Perm != plain.Perm || now.Rc != plain.Rc;
        ChevronButton.Foreground = BrushOf(changed ? _theme.Accent : _theme.Hint);
    }

    /// <summary>
    /// The model list check writes the lists to the file in the background; keep them current in memory too, or the next save
    /// (a launch, a settings change) wrote the old ones back (review 10-03).
    /// </summary>
    private void TakeBackgroundModelLists()
    {
        if (_store.TryLoad() is { } fresh)   // not when the file couldn't be read: defaults would be saved over the lists
        {
            _settings.Models = fresh.Models;
            _settings.CodexModels = fresh.CodexModels;
            _settings.ModelsChecked = fresh.ModelsChecked;
        }
    }

    private void OnSettingsChanged()
    {
        ApplyNativeSearchBox();
        _system?.Apply(WatchOptions());
        TakeBackgroundModelLists();
        _store.Save(_settings);
        PaintChevron();
        if (!_typing || !UsableModes().Contains(_mode))
        {
            SetMode(HomeMode());   // if the order changed or file search was turned off, go back to the default mode
        }

        UpdateLeadIcon();
        Entry.PlaceholderText = Placeholders.For(_mode, _settings.Agent);
    }

    private void OnLanguageChanged()
    {
        _language = LanguageResolver.Resolve(_settings.Lang, CurrentUiLanguageId());
        _text = LanguageCatalog.For(_language);
        ApplyFonts();
        _settingsWindow?.Reload(_text);
    }

    /// <summary>Top edge of the taskbar (screen coordinates). The options window opens above it.</summary>
    private static int TaskbarTop()
    {
        if (Interop.NativeMethods.GetWindowRect(Interop.TaskbarHost.FindTaskbar(), out var rect))
        {
            return rect.Top;
        }

        var area = Microsoft.UI.Windowing.DisplayArea.Primary.WorkArea;
        return area.Y + area.Height;
    }

    /// <summary>
    /// Left icon: the spark extracted from claude.exe in Claude mode (same as the Python version), a magnifier in file mode.
    /// When an island (progress) is showing, the island decides the icon.
    /// </summary>
    private void UpdateLeadIcon()
    {
        var island = _snapshot.IsActive && !_typing && _compose is null;   // composing counts as typing (agent mark, not the island icon)
        var agentIcon = !island && _mode == BarMode.Claude;
        var spark = agentIcon && _settings.Agent == AgentKind.Claude && _claudeGlyph is not null;
        var blossom = agentIcon && _settings.Agent == AgentKind.Codex && CodexGlyph.Source is not null;
        ClaudeGlyphImage.Source = _claudeGlyph;
        ClaudeGlyphImage.Visibility = spark ? Visibility.Visible : Visibility.Collapsed;
        CodexGlyph.Visibility = blossom ? Visibility.Visible : Visibility.Collapsed;
        LeadIcon.Visibility = spark || blossom ? Visibility.Collapsed : Visibility.Visible;
        if (!island)
        {
            LeadIcon.Glyph = ModeGlyph(_mode);
        }
    }

    /// <summary>
    /// Dragging a file onto the pill runs the drop command on it when one is set (e.g. a script that sends to a phone — PhoneDrop),
    /// otherwise attaches it to the prompt. Blue border while hovering; on transfer, the island shows it sending.
    /// </summary>
    private void HookFileDrop()
    {
        Root.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                return;
            }

            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            e.DragUIOverride.IsCaptionVisible = false;
            e.DragUIOverride.IsGlyphVisible = false;
            if (!_dropHover)
            {
                _dropHover = true;
                _dropNames = null;
                RenderIsland();
                _ = ReadDropNamesAsync(e.DataView);
            }
        };
        Root.DragLeave += (_, _) =>
        {
            _dropHover = false;
            RenderIsland();
        };
        Root.Drop += async (_, e) =>
        {
            _dropHover = false;
            RenderIsland();
            if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                return;
            }

            // The drop command decides what a drop does: if one is set (e.g. a script that sends to a phone),
            // run it with the dropped files; if it's empty, attach the files to the prompt instead (the default for everyone). (user 10-01)
            var hasCommand = !string.IsNullOrWhiteSpace(_settings.DropCommand);

            var deferral = e.GetDeferral();
            try
            {
                var items = await e.DataView.GetStorageItemsAsync();
                var paths = items.Select(i => i.Path).Where(p => !string.IsNullOrEmpty(p)).ToList();
                if (hasCommand)
                {
                    if (!_phoneDrop.Send(paths, _settings.DropCommand))
                    {
                        TaskbarHost.Log($"drop→command: nothing to run or not files ({paths.Count})");
                    }
                }
                else
                {
                    AttachToPrompt(paths);
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)   // async void: anything escaping ends the app (review 10-03)
            {
                TaskbarHost.Log("drop failed: " + ex.Message);
            }
            finally
            {
                deferral.Complete();
            }
        };
    }

    /// <summary>Fills in the dragged files' names for the drop hint (read asynchronously, so the hint shows at once and the names follow).</summary>
    private async Task ReadDropNamesAsync(Windows.ApplicationModel.DataTransfer.DataPackageView view)
    {
        try
        {
            var items = await view.GetStorageItemsAsync();
            if (items.Count == 0 || !_dropHover)
            {
                return;
            }

            _dropNames = items[0].Name + (items.Count > 1 ? $" +{items.Count - 1}" : string.Empty);
            RenderIsland();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
        }
    }

    /// <summary>
    /// Attach dropped files to the prompt: put their paths into the input box (quoted when they contain spaces) and start typing,
    /// so the person can add their message and press Enter. The agent reads the referenced paths. (10-01)
    /// </summary>
    private void AttachToPrompt(IReadOnlyList<string> paths)
    {
        var files = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (files.Count == 0)
        {
            return;
        }

        if (_mode != BarMode.Claude && UsableModes().Contains(BarMode.Claude))
        {
            SetMode(BarMode.Claude);
        }

        var refs = string.Join(" ", files.Select(p => p.Contains(' ', StringComparison.Ordinal) ? $"\"{p}\"" : p));
        var gap = Entry.Text.Length > 0 && !Entry.Text.EndsWith(' ') ? " " : string.Empty;
        Entry.Text += gap + refs + " ";
        BeginTyping();
        Entry.Focus(FocusState.Programmatic);
        Entry.SelectionStart = Entry.Text.Length;
    }

    /// <summary>
    /// Mic/camera in use: orange dot at the far right (like a phone's privacy dot). It sits in the same row as the music bars etc.
    /// so they don't overlap (user feedback 09-30: the dot overlapped the graph — fix without hurting the look). Visible while typing too.
    /// </summary>
    private bool _privacyOn;

    private void UpdatePrivacyDot()
    {
        var on = _system is { } s && (s.MicInUse || s.CameraInUse);
        if (on != _privacyOn)
        {
            _privacyOn = on;
            TaskbarHost.Log($"privacy dot {(on ? "on" : "off")}: {_system?.PrivacyApp}");
        }

        PrivacyDot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        var sig = on ? $"{Attention()}|{_system!.PrivacyApp}" : null;
        if (on && sig != _privacySig)
        {
            // only on a change: a new brush and tooltip 4 times a second piled up native objects while the mic was on (review 10-03)
            PrivacyDot.Fill = BrushOf(Attention());
            Microsoft.UI.Xaml.Controls.ToolTipService.SetToolTip(PrivacyDot, _system!.PrivacyApp);
        }

        _privacySig = sig;
    }

    private string? _privacySig;

    private IsleBar.Core.Island.PomodoroLengths PomodoroLengths()
        => IsleBar.Core.Island.PomodoroLengths.FromMinutes(_settings.PomodoroFocus, _settings.PomodoroBreak, _settings.PomodoroLong);

    /// <summary>
    /// Advance Pomodoro phases: after focus (green border 3s) comes a break, after a break the next focus round, and after round 4's long break it ends.
    /// Rewrites the same file, so the island treats it as "the same item" and doesn't jump — only the text and ring change.
    /// </summary>
    private void AdvancePomodoros(List<ActivityState> items)
    {
        var now = DateTimeOffset.UtcNow;
        for (var i = 0; i < items.Count; i++)
        {
            var state = items[i];
            if (state.Kind != ActivityKind.Timer || TimerParser.PomodoroPhase(state) is null || state.SourcePath is not { } path)
            {
                continue;
            }

            var next = TimerParser.AdvancePomodoro(state, now, _text.Focus, _text.Break, PomodoroLengths());
            if (ReferenceEquals(next, state))
            {
                continue;
            }

            // write the next phase first, then ring: a write that failed (file locked) used to ring again every tick (review 10-03)
            var slot = Path.GetFileNameWithoutExtension(path);
            try
            {
                if (next is null)
                {
                    File.Delete(path);
                    items.RemoveAt(i--);
                }
                else
                {
                    next.SourcePath = _activities.Write(slot, next);
                    items[i] = next;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;   // tried again next tick, still silent
            }

            PlayChime(alarm: true);   // a pomodoro phase just ended (focus → break, or the whole set finished)
        }
    }

    private readonly HashSet<string> _chimed = new();   // timer slots we have already sounded, so a finished timer chimes once, not every tick

    /// <summary>
    /// Rings once the moment a plain (non-pomodoro) timer reaches zero. Pomodoro phase-ends chime from <see cref="AdvancePomodoros"/>.
    /// The slot is remembered so it does not ring every tick while the finished timer sits there; the memory is cleared when the
    /// timer file is gone (dismissed) so the same slot can ring again next time (user 09-30: play a sound when timers finish).
    /// </summary>
    private void ChimeFinishedTimers(List<ActivityState> items)
    {
        var now = DateTimeOffset.UtcNow;
        var present = new HashSet<string>();
        foreach (var state in items)
        {
            if (state.Kind != ActivityKind.Timer || state.SourcePath is not { } path || TimerParser.PomodoroPhase(state) is not null)
            {
                continue;   // pomodoros ring from AdvancePomodoros; stopwatches have no end
            }

            var slot = Path.GetFileNameWithoutExtension(path);
            present.Add(slot);
            var finished = !state.IsPaused && !TimerParser.IsStopwatch(state) && TimerRemaining(state, now) <= TimeSpan.Zero;
            if (finished && _chimed.Add(slot))
            {
                PlayChime(alarm: true);
            }
        }

        _chimed.RemoveWhere(slot => !present.Contains(slot));   // a dismissed timer can ring again if it comes back
    }

    private readonly HashSet<string> _chimedDone = new();   // agent-done states already sounded, so a finished task chimes once

    /// <summary>
    /// Rings once the moment an agent task finishes. The completion already outranks music on the pill, but a silent visual change
    /// is easy to miss while looking elsewhere (browsing, music playing) — a sound means you notice it (user 10-01).
    /// Keyed by the state file, and forgotten when it ages out, so a later run rings again.
    /// </summary>
    private void ChimeFinishedAgents(List<ActivityState> items)
    {
        var present = new HashSet<string>();
        var ring = false;
        foreach (var state in items)
        {
            if (state.Kind != ActivityKind.AgentDone || state.SourcePath is not { } path || !File.Exists(path))
            {
                continue;   // (a state cleared earlier in this tick — e.g. its terminal was closed — stays silent)
            }

            // Keyed by file + when it was written, so a second completion in the same session (rewriting the same file) rings too.
            var key = path + "|" + state.UpdatedAt.UtcTicks;
            present.Add(key);
            // Ring once per finished task. (We tried muting it while a terminal was focused, but it kept swallowing the sound during
            // testing and normal use — the user wants to hear every completion; 10-01.) The card/border still shows on the pill too.
            ring |= _chimedDone.Add(key);
        }

        // Completions already there when the bar starts (they linger 12 h) were rung when they happened — a restart
        // (or a watchdog relaunch) must not ring them all again (code review 10-01).
        if (ring && _chimeSeeded)
        {
            PlayChime();
        }

        _chimeSeeded = true;
        _chimedDone.RemoveWhere(p => !present.Contains(p));
    }

    private bool _chimeSeeded;

    // Terminal window of each finished task's session, looked up once per state file (finding it briefly attaches to the console).
    // A failed lookup (no visible terminal — e.g. an editor's built-in terminal) is retried after a while rather than kept forever.
    private readonly Dictionary<string, IntPtr> _doneWindows = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _doneWindowRetry = new(StringComparer.OrdinalIgnoreCase);
    private readonly Interop.TerminalInputWatch _terminalInput = new();

    /// <summary>
    /// A finished task's green border clears once you've actually gone back to its terminal — clicked it or typed in it — or when that
    /// terminal is closed (user 10-01). Merely having the window in front doesn't count: it can be left open while you're away, and
    /// if you were watching it when the task ended the green vanished before you could see it. ✕, a click on the bar and the next
    /// prompt still clear it too.
    /// </summary>
    private void DismissSeenAgents(List<ActivityState> items)
    {
        var present = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in items)
        {
            // A question/permission prompt whose terminal was closed can't be answered any more — clear the orange too.
            // Same for "working": closing the window kills the session before its SessionEnd hook can run, which left a dead
            // session "working" for 6 h (found 10-01 when a terminal holding three sessions closed).
            if (state is { Kind: ActivityKind.AgentPermission or ActivityKind.AgentWorking, SourcePath: { } asked, Pid: { } askPid }
                && !ProcessAlive(askPid))
            {
                TaskbarHost.Log($"{(state.Kind == ActivityKind.AgentWorking ? "working" : "permission")} cleared: {Path.GetFileName(asked)} (its terminal was closed)");
                DeleteState(asked);
                continue;
            }

            if (state.Kind != ActivityKind.AgentDone || state.SourcePath is not { } path || state.Pid is not { } pid)
            {
                continue;
            }

            if (!ProcessAlive(pid))
            {
                TaskbarHost.Log($"done cleared: {Path.GetFileName(path)} (its terminal was closed)");
                DeleteState(path);
                continue;
            }

            present.Add(path);
            if (!_doneWindows.TryGetValue(path, out var window)
                || (window == IntPtr.Zero && DateTimeOffset.UtcNow >= _doneWindowRetry.GetValueOrDefault(path)))
            {
                window = AgentWindow.Find(pid);
                _doneWindows[path] = window;
                _doneWindowRetry[path] = DateTimeOffset.UtcNow.AddSeconds(30);
            }

            if (window != IntPtr.Zero && _terminalInput.TakeTouched(window))
            {
                TaskbarHost.Log($"done seen: {Path.GetFileName(path)} (clicked or typed in its terminal)");
                DeleteState(path);
                present.Remove(path);
            }
        }

        foreach (var gone in _doneWindows.Keys.Where(p => !present.Contains(p)).ToList())
        {
            _doneWindows.Remove(gone);
            _doneWindowRetry.Remove(gone);
        }

        // Hooks only while a finished task is waiting (installing/removing only when that changes)
        var windows = _doneWindows.Values.Where(w => w != IntPtr.Zero).ToHashSet();
        if (!windows.SetEquals(_watchedWindows))
        {
            _watchedWindows = windows;
            _terminalInput.Watch(windows);
        }
    }

    private HashSet<IntPtr> _watchedWindows = [];

    private static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return true;   // it exists but we may not look inside (e.g. an agent in an Administrator terminal) — not "gone" (code review 10-02)
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Plays the Windows notification sound (falls back to a plain beep if none is registered).</summary>
    /// <summary>
    /// The chime — not during Do Not Disturb, and not over a full-screen game/video when the person asked for that (10-03).
    /// The pill itself still changes; only the sound is held back.
    /// </summary>
    /// <param name="alarm">A timer or pomodoro the user set: it rings through Do Not Disturb and full screen, like Windows' own
    /// alarms — only an agent's completion is held back (review 10-03).</param>
    private void PlayChime(bool alarm = false)
    {
        if (!alarm && NativeMethods.SHQueryUserNotificationState(out var quiet) == 0
            && (quiet == NativeMethods.QUNS_QUIET_TIME
                || (_settings.MuteChimeFullscreen && quiet is NativeMethods.QUNS_BUSY or NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN
                                                       or NativeMethods.QUNS_PRESENTATION_MODE)))
        {
            TaskbarHost.Log($"chime: quiet (notification state {quiet})");
            return;
        }

        if (!alarm && NativeMethods.DoNotDisturbOn())
        {
            TaskbarHost.Log("chime: quiet (Do Not Disturb)");
            return;
        }

        if (!NativeMethods.PlaySound("Notification.Default", IntPtr.Zero, NativeMethods.SND_ASYNC | NativeMethods.SND_ALIAS | NativeMethods.SND_NODEFAULT))
        {
            NativeMethods.MessageBeep(0xFFFFFFFF);
        }
    }

    private ExpandedNotice? _expanded;
    private DateTimeOffset _lastExpandedAt = DateTimeOffset.MinValue;   // high-water mark so an older notice resurfacing (e.g. after X dismisses a newer one) doesn't re-open the card

    /// <summary>
    /// A message (Windows notification, KakaoTalk) grows out of the pill as a card, then shrinks back; the pill keeps the
    /// compact line for the rest of the notice's time. Not while typing (the card would cover what you are writing).
    /// </summary>
    /// <summary>
    /// Grows the expanded card for every message notice we haven't shown yet — fed the whole activity list, not just the
    /// primary one, so a burst of messages all reach the card and stack (relying on the single primary dropped the middle
    /// ones — user 09-30). The high-water mark on UpdatedAt means each message opens once, and an older one resurfacing
    /// (e.g. after the pill's X removes a newer one) does not re-open it.
    /// </summary>
    private void ExpandIfMessage(IReadOnlyList<ActivityState> items)
    {
        if (_typing)
        {
            return;
        }

        var fresh = items
            .Where(s => s is { Kind: ActivityKind.Notice, Stage: "expand" } && s.UpdatedAt > _lastExpandedAt)
            .OrderBy(s => s.UpdatedAt)
            .ToList();
        if (fresh.Count == 0)
        {
            return;
        }

        // Mark these as shown NOW so the next tick doesn't re-feed them, but show the card OUTSIDE this tick: showing a card
        // creates/moves windows and runs composition, which pumps the message loop and stalled the island timer (the pill and
        // card then froze after a couple of messages — root cause found 09-30). Decoupling it keeps the tick fast and alive.
        _lastExpandedAt = fresh[^1].UpdatedAt;
        _ui.TryEnqueue(() =>
        {
            try
            {
                ExpandCards(fresh);
            }
            catch
            {
                // showing the card must never take the island timer down with it
            }
        });
    }

    private void ExpandCards(List<ActivityState> fresh)
    {
        if (_expanded is null)
        {
            _expanded = new ExpandedNotice();
            AppFonts.Apply(_expanded, _language);
        }

        foreach (var state in fresh)
        {
            _expanded.Show(
                new Windows.Graphics.RectInt32(Spot.X, Spot.Y, _width, _height),
                state.Glyph ?? "",
                state.Title ?? string.Empty,
                state.Msg,
                state.Name,
                state.Open,
                state.AppId);
            _lastExpandedAt = state.UpdatedAt;
        }
    }

    /// <summary>Mode icon (Segoe Fluent Icons) — shared by the left icon and the Tab hint.</summary>
    private static string ModeGlyph(BarMode mode) => mode switch
    {
        BarMode.Files => "",   // folder — file search (Everything). User feedback 09-30: icons must not be confusing
        BarMode.Web => "",     // magnifier — web search
        _ => "",               // chat (when there's no Codex icon)
    };

    /// <summary>
    /// Tab hint (09-30 design decision B): <b>only when the input box is empty</b>, a dim [Tab] key + the next mode's icon on the right.
    /// Shows that Tab switches function, but disappears once you start typing so it doesn't get in the way (user asked for a hint that Tab changes the function).
    /// </summary>
    private void UpdateTabHint()
    {
        var next = BarModes.Next(_mode, UsableModes(), filesOn: true);
        var show = _typing && Entry.Text.Length == 0 && next != _mode && _pendingTimer is null;   // the "replace timer?" question needs the whole width
        TabHint.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (!show)
        {
            return;
        }

        var hint = BrushOf(_theme.Hint);
        TabKey.BorderBrush = hint;
        TabKeyText.Foreground = hint;
        TabNextIcon.Foreground = hint;
        TabNextIcon.Glyph = ModeGlyph(next);

        // if next is Claude/Codex, use the same mark as the left icon (from the user's PC; glyph when there is none)
        var mark = next == BarMode.Claude
            ? (_settings.Agent == AgentKind.Codex ? CodexGlyph.Source : _claudeGlyph)
            : null;
        TabNextMark.Source = mark;
        TabNextMark.Visibility = mark is null ? Visibility.Collapsed : Visibility.Visible;
        TabNextIcon.Visibility = mark is null ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Closes the results list window (recreated on the next search).</summary>
    private void CloseResults()
    {
        _results?.Close();
        _results = null;
    }

    private bool MoveResultSelection(int delta) => _results?.Move(delta) ?? true;

    /// <summary>Opens the selected result. Ctrl+Enter selects the file in Explorer instead.</summary>
    private void OpenSelectedFile(bool revealInFolder)
    {
        if (_results?.SelectedHit is not { } hit || _shownQuery != Entry.Text.Trim())
        {
            // if the list hasn't caught up with the current input, search with the current input now and open the first item (same as the Python version)
            _openWhenReady = revealInFolder ? OpenRequest.Reveal : OpenRequest.Open;
            RequestSearch(Entry.Text.Trim());
            return;
        }

        OpenHit(hit, revealInFolder);
    }

    private void OpenHit(FileHit hit, bool revealInFolder)
    {
        TaskbarHost.Log($"file search: open {(revealInFolder ? "(reveal) " : string.Empty)}{System.IO.Path.GetExtension(hit.FullPath)}");
        try
        {
            if (revealInFolder)
            {
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{hit.FullPath}\"");
            }
            else
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(hit.FullPath) { UseShellExecute = true });
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            TaskbarHost.Log("file search: open failed — " + ex.Message);
            if (NativeMethods.GetForegroundWindow() != _taskbar.OuterWindow)
            {
                EndTyping();   // clicked with the mouse: the bar is no longer in front, so typing would never end (review 10-03)
            }

            return;
        }

        Entry.Text = string.Empty;
        EndTyping();
    }

    private static bool IsControlDown()
        => Microsoft.UI.Input.InputKeyboardSource
            .GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void ScheduleSearch()
    {
        if (_searchTimer is null)
        {
            // attach the handler only once (attaching on every call fires multiple searches)
            _searchTimer = _ui.CreateTimer();
            _searchTimer.Interval = SearchDebounce;
            _searchTimer.IsRepeating = false;
            _searchTimer.Tick += (_, _) => RequestSearch(Entry.Text.Trim());
        }

        _searchTimer.Stop();
        _searchTimer.Start();
    }

    private void RequestSearch(string query)
    {
        _requestedQuery = query;
        _fileSearch.Request(query);
    }

    /// <summary>Called from the search thread → <b>marshals to the UI thread</b> to draw. Results for stale input are dropped.</summary>
    private void OnSearchResult(long seq, IReadOnlyList<FileHit> hits, SearchProblem problem)
        => _ui.TryEnqueue(() =>
        {
            if (seq != _fileSearch.LatestSeq || _mode != BarMode.Files)
            {
                return;
            }

            if (_openWhenReady is { } open)
            {
                _openWhenReady = null;
                if (hits.Count > 0)
                {
                    OpenHit(hits[0], open == OpenRequest.Reveal);
                    return;
                }
            }

            var query = Entry.Text.Trim();
            string? note = problem switch
            {
                SearchProblem.SdkMissing => _text.NoSdk,
                SearchProblem.ServiceOff => _text.EsOff,
                SearchProblem.Failed => _text.EsFail,
                _ => hits.Count == 0 && query.Length > 0 ? _text.NoResult : null,
            };

            if (query.Length == 0 || (hits.Count == 0 && note is null))
            {
                CloseResults();
                return;
            }

            if (_results is null)
            {
                _results = new ResultsWindow();
                AppFonts.Apply(_results, _language);
                _results.RowClicked += index =>
                {
                    if (_results?.Move(index - _results.Selected) == true && _results.SelectedHit is { } hit)
                    {
                        OpenHit(hit, IsControlDown());
                    }
                };
            }

            _results.Show(hits, note, PopupAnchorX, TaskbarTop());
            _shownQuery = _requestedQuery;   // the latest request's results (older ones were dropped above)
        });

    /// <summary>Windows display language (LANGID). The resolution rules live in Core (LanguageResolver).</summary>
    private static int CurrentUiLanguageId() => GetUserDefaultUILanguage();

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    private enum OpenRequest { Open, Reveal }

    private async Task UpdateModelAliasesAsync()
    {
        using var source = new HttpModelAliasSource();
        var update = await new ModelAliasUpdater(_store, source).RunOnceAsync(DateTimeOffset.UtcNow);
        if (update.Changed)
        {
            RequestExit(ExitCodes.RestartRequested);   // relaunch with the new buttons
        }
    }

    private const int MenuDismissBase = 100;
    private const int MenuQuit = 1;
    private const int MenuQuickBase = 20;   // quick timers (from settings, up to 6)
    private const int MenuPomodoro = 13;
    private const int MenuStopwatch = 14;
    private const int MenuUpdate = 15;
    private const int MenuNotifications = 16;   // opens the Windows notification centre   // "Download update (x.y.z)" while a newer release is out

    /// <summary>Timer file name — one at a time (starting a new one replaces it; same slot as typing "25min" in the input box).</summary>
    private static string TimerSlot => $"timer_{Environment.ProcessId}";

    /// <summary>A new timer waiting for "Replace the running timer?" to be answered (Enter = replace, Esc = keep).</summary>
    private ActivityState? _pendingTimer;

    /// <summary>
    /// Starts a timer — unless one is already running (or paused), in which case the input box asks first instead of silently
    /// throwing it away: starting a stopwatch used to wipe a running Pomodoro without a word (user chose "ask once", 10-01).
    /// True if it started now; false if it's waiting for the answer.
    /// </summary>
    private bool StartTimer(ActivityState next)
    {
        var now = DateTimeOffset.UtcNow;
        var running = _activities.Read(now).Any(s => s.Kind == ActivityKind.Timer
            && (s.IsPaused || TimerParser.IsStopwatch(s) || TimerParser.Remaining(s, now) > TimeSpan.Zero));
        if (!running)
        {
            ReplaceTimer(next);
            return true;
        }

        _pendingTimer = next;
        if (!_typing)
        {
            _taskbar.TakeFocus();
            BeginTyping();
        }

        Entry.Text = string.Empty;
        Entry.PlaceholderText = _text.ReplaceTimerAsk;
        UpdateTabHint();
        return false;
    }

    /// <summary>Writes the timer, removing any other timer first — one left behind by an earlier bar process would otherwise stay too.</summary>
    private void ReplaceTimer(ActivityState next)
    {
        foreach (var old in _activities.Read(DateTimeOffset.UtcNow).Where(s => s.Kind == ActivityKind.Timer && s.SourcePath is not null))
        {
            DeleteState(old.SourcePath!);
        }

        _activities.Write(TimerSlot, next);
    }

    /// <summary>
    /// Right-click menu. A "Dismiss: …" entry for each island item currently shown (file-backed ones only — timers, done, transfers, notices),
    /// and "Turn off search bar" at the bottom. Music is controlled by the player app and can't be dismissed, so it's left out.
    /// </summary>
    /// <summary>
    /// A click on the bar always starts typing, whatever the island shows (user 10-01: "Claude working" swallowed the click by jumping to
    /// a terminal that was already in front, so it took ✕ and a second click to get a cursor). When typing ends, the island comes back
    /// as it was. A finished task counts as seen on that click and clears (✕ still dismisses without typing).
    /// Opening a message's app stays on the expanded message card. Always returns false (the click goes on to start typing).
    /// </summary>
    private bool TryIslandAction()
    {
        if (_snapshot.IsActive && _snapshot.Primary is { Kind: ActivityKind.AgentDone })
        {
            DismissPrimary();
            return false;
        }

        return TryAcknowledgeBackgroundDone();
    }

    /// <summary>
    /// Music (or another item) holds the pill while a task finished behind it (only the green border shows) — a click on the bar
    /// counts as having seen it: the completion (border) clears and typing starts in the same click, like the primary case (user 10-01).
    /// Always returns false so the click goes on to start typing.
    /// </summary>
    private bool TryAcknowledgeBackgroundDone()
    {
        // only when its green border is what shows: under an orange "needs an answer" or a red notice (a usage limit holds the
        // pill for hours) the completion was never seen — a click there deleted it unseen (review 10-03)
        if (_backgroundDone is { SourcePath: { } path } && _backgroundDoneKey is not null && _glowKey == _backgroundDoneKey)
        {
            DeleteState(path);
            _backgroundDone = null;
            _backgroundDoneKey = null;
            UpdateGlow();
        }

        return false;
    }

    private static void DeleteState(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>While the right-click menu is open (blocks starting to type).</summary>
    private bool _menuOpen;

    private void ShowContextMenu()
    {
        var live = _activities.Read(DateTimeOffset.UtcNow)
            .Where(a => a.SourcePath is { } path && File.Exists(path))
            .ToList();
        var items = new List<(int, string?)>();
        for (var i = 0; i < live.Count; i++)
        {
            items.Add((MenuDismissBase + i, $"{_text.Dismiss}: {DescribeForMenu(live[i])}"));
        }

        if (items.Count > 0)
        {
            items.Add((0, null));
        }

        var quick = _settings.QuickTimers.Take(6).ToList();
        for (var q = 0; q < quick.Count; q++)
        {
            items.Add((MenuQuickBase + q, $"{_text.Timer} {string.Format(System.Globalization.CultureInfo.InvariantCulture, _text.Minutes, quick[q])}"));
        }

        items.Add((MenuPomodoro, _text.Pomodoro));
        items.Add((MenuStopwatch, _text.Stopwatch));
        items.Add((0, null));
        var update = _settings.CheckUpdates ? SystemWatch.UpdateWatcher.Available : null;
        if (update is { } u)
        {
            items.Add((MenuUpdate, $"{_text.UpdateMenu} ({u.Version.ToString(3)})"));   // a newer release is out (10-01)
        }

        items.Add((MenuNotifications, _text.AllNotifications));
        items.Add((MenuQuit, _text.Quit));

        // A window attached to the taskbar can't become the foreground window, so the menu couldn't receive clicks (measured on PC 09-29) →
        // detach and bring it forward, as when typing, only while the menu is open. Position and shape are unchanged, so it's unnoticeable.
        var detached = false;
        if (!_typing && _taskbar.IsAttached)
        {
            _taskbar.Detach(Spot.X, Spot.Y, _width, _height);
            ApplyRegion();
            detached = true;
        }

        _menuOpen = true;
        int chosen;
        try
        {
            chosen = ContextMenu.Show(_taskbar.OuterWindow, items);
        }
        finally
        {
            // focus notifications can arrive late after the menu closes, so release one beat later
            _ui.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            {
                if (!_typing)
                {
                    FocusSink.Focus(FocusState.Programmatic);   // so the input box isn't left with just a blinking caret
                }

                _menuOpen = false;
            });
        }

        if (detached && !_typing && _taskbar.Attach())
        {
            PlaceOnTaskbar();
            ApplyRegion();
        }

        var now = DateTimeOffset.UtcNow;
        if (chosen == MenuQuit)
        {
            RequestExit(ExitCodes.UserQuit);
        }
        else if (chosen == MenuNotifications)
        {
            // the full list is Windows' own notification centre (user 10-03: a list of our own would only duplicate it).
            // Win+N opens it; the "ms-actioncenter:" link did nothing on Windows 11 (measured 10-03).
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, 0, IntPtr.Zero);
            NativeMethods.keybd_event((byte)'N', 0, 0, IntPtr.Zero);
            NativeMethods.keybd_event((byte)'N', 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
            NativeMethods.keybd_event(NativeMethods.VK_LWIN, 0, NativeMethods.KEYEVENTF_KEYUP, IntPtr.Zero);
        }
        else if (chosen == MenuUpdate && update is not null)
        {
            SystemWatch.Updater.Start();   // one-click update: download, check, install, restart
        }
        else if (chosen >= MenuQuickBase && chosen - MenuQuickBase < quick.Count)
        {
            StartTimer(TimerParser.ToActivity(TimeSpan.FromMinutes(quick[chosen - MenuQuickBase]), now));
        }
        else if (chosen == MenuPomodoro)
        {
            StartTimer(TimerParser.ToPomodoro(1, focus: true, now, _text.Focus, _text.Break, PomodoroLengths()));
        }
        else if (chosen == MenuStopwatch)
        {
            StartTimer(TimerParser.ToStopwatch(now, _text.Stopwatch));
        }
        else if (chosen >= MenuDismissBase && chosen - MenuDismissBase < live.Count)
        {
            try
            {
                File.Delete(live[chosen - MenuDismissBase].SourcePath!);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    /// <summary>Short label for the menu: "⏱ 24:59" · "Claude done · islebar" · "📥 a.jpg".</summary>
    private string DescribeForMenu(ActivityState state)
    {
        var now = DateTimeOffset.UtcNow;
        return state.Kind switch
        {
            ActivityKind.Timer when TimerParser.Elapsed(state, now) is { } went => $"⏱ {FormatClock(went)}",
            ActivityKind.Timer => TimerRemaining(state, now) is { } left && left > TimeSpan.Zero
                ? $"⏱ {FormatClock(left)}"
                : $"⏱ {_text.Done}",
            ActivityKind.AgentDone => AgentMenuLabel(state, _text.AgentDone),
            ActivityKind.AgentPermission => AgentMenuLabel(state, _text.NeedsAnswer),
            // "working" fell through to the transfer arrow ("Dismiss: ↑ session") — design check 10-01
            ActivityKind.AgentWorking => AgentMenuLabel(state, _text.Working),
            _ => $"{DirectionGlyph(state)} {state.Name}",
        };
    }

    /// <summary>"Claude done · name", or without the trailing " · " when the session has no name.</summary>
    private static string AgentMenuLabel(ActivityState state, string word)
        => string.IsNullOrEmpty(state.Name) ? $"{state.AgentLabel} {word}" : $"{state.AgentLabel} {word} · {state.Name}";

    private void RequestExit(int code)
    {
        App.ExitCode = code;

        // When Explorer restarts, the window inside the taskbar is destroyed with it, the UI thread never gets this request, and we were left as a windowless process
        // (measured on an Explorer restart 09-30: hung after "restart: window gone"). If it doesn't finish within 4s, exit here → the watchdog relaunches us.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
            Environment.Exit(code);
        });
        _ui.TryEnqueue(() =>
        {
            _watchdog.Quitting = true;
            _taskbar.ReleaseForClose();   // a re-parented window can't be closed by WinUI without a fail-fast (see ReleaseForClose)
            Close();
        });
    }

    private void Cleanup()
    {
        _watchdog.Quitting = true;
        // every window closed, so the app ends by itself instead of waiting 4 s for the forced exit (review 10-03) — first, while
        // the meter and timers still exist (closing compose re-renders the pill)
        foreach (var close in new Action?[] { _settingsWindow is null ? null : _settingsWindow.Close, _expanded is null ? null : _expanded.CloseAll, _preview is null ? null : _preview.Close, _compose is null ? null : _compose.Close })
        {
            try
            {
                close?.Invoke();
            }
            catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
            {
            }
        }

        _hotkey?.Dispose();
        _media.Dispose();
        _downloads.Dispose();
        StopMeterBars();
        _audioMeter.Dispose();
        if (_settings.NativeSearchBox)
        {
            NativeSearchBox.Show();   // the module restores itself when the process exits too, but on a normal exit do it right away
        }
        _system?.Dispose();
        _terminalInput.Dispose();
        CloseResults();
        _flyout?.Close();
        _settingsWindow?.Close();

        _islandTimer?.Stop();
        _searchTimer?.Stop();
        _watchdog.Dispose();
        _tracker.Dispose();
        _fileSearch.Dispose();
    }
}
