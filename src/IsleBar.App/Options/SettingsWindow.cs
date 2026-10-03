using IsleBar.App.Interop;
using IsleBar.Core.Configuration;
using IsleBar.Core.Launch;
using IsleBar.Core.Localization;
using IsleBar.Core.Search;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace IsleBar.App.Options;

/// <summary>
/// Settings window. The same items as the Python version's <c>open_settings</c> (language · launch options and pins · working folder · file search)
/// plus the <b>agent (Claude / Codex) picker</b> built in server step 11.
/// Saves immediately on every change (no separate "OK" button — same as the Windows Settings app).
/// </summary>
internal sealed class SettingsWindow : Window
{
    private const int WidthDip = 460;

    private readonly IsleBarSettings _settings;
    private readonly Action _changed;
    private readonly Action _languageChanged;
    private readonly Func<string> _currentLanguage;
    private readonly StackPanel _body = new() { Padding = new Thickness(20, 8, 20, 20), Spacing = 6 };
    private LanguageStrings _text;

    public SettingsWindow(IsleBarSettings settings, LanguageStrings text, Action changed, Action languageChanged, Func<string> currentLanguage)
    {
        _currentLanguage = currentLanguage;
        Closed += (_, _) =>
        {
            foreach (var commit in _commitOnClose.ToList())
            {
                commit();
            }
        };
        _settings = settings;
        _text = text;
        _changed = changed;
        _languageChanged = languageChanged;
        SystemBackdrop = new MicaBackdrop();

        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = false;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = false;
        }

        // Title is just "Settings", no icon or taskbar button (user feedback 09-30: drop "IsleBar", it looks cheap)
        AppWindow.TitleBar.IconShowOptions = IconShowOptions.HideIconAndSystemMenu;
        AppWindow.IsShownInSwitchers = false;

        // A stock ToggleSwitch puts a 10 dip empty row above and below the knob (space meant for On/Off text we don't use —
        // these are x:Double row heights in the default template, not margins). So every toggle row (island notices, appearance,
        // the "remote control" option, Tab-order switches) stood ~20 dip taller than the segmented-button rows beside it. That made
        // the launch options look lopsided — "remote control" floated with a big gap above and was crammed against the next section
        // below — and bloated the whole window past the screen height so it scrolled (user feedback 10-01). Trimming these two
        // heights to 4 dip makes toggle rows the same compact height as the button rows, so the spacing reads evenly.
        // (Applied on _body so every toggle under it inherits it; the knob keeps breathing room from the template's own 0,5 margin.)
        _body.Resources["ToggleSwitchPreContentMargin"] = 4.0;
        _body.Resources["ToggleSwitchPostContentMargin"] = 4.0;

        Content = new ScrollViewer { Content = _body };
        Build();
    }

    private int _anchorX;
    private int _bandTop;

    public void ShowAbove(int anchorX, int bandTop)
    {
        (_anchorX, _bandTop) = (anchorX, bandTop);
        PopupPlacement.ShowSized(this, _body, WidthDip, anchorX, bandTop);
    }

    /// <summary>After the item count changes (agent switch, language change), re-fit the window size and position.</summary>
    private void Refit()
    {
        _body.UpdateLayout();
        PopupPlacement.SizeToContent(this, _body, WidthDip);
        PopupPlacement.PlaceAbove(this, _anchorX, _bandTop);
    }

    /// <summary>When the language changes, fetch the strings again and redraw.</summary>
    public void Reload(LanguageStrings text)
    {
        _text = text;
        Build();
        Refit();
    }

    // the text rows of the window as built now — a rebuild (agent, mode or language switch) replaces them, so a closed window
    // commits only what is on screen (the old rows' stale values reverted saved edits — review 10-03)
    private readonly List<Action> _commitOnClose = [];

    private void Build()
    {
        _commitOnClose.Clear();
        BuildBody();
        IslandUi.AppFonts.Apply(this, _language);   // on every redraw (changing the language also changes the Han-character font)
    }

    private void BuildBody()
    {
        Title = _text.Settings;
        _body.Children.Clear();

        // Language
        Section(_text.Lang);
        var lang = new ComboBox { MinWidth = 180 };
        lang.Items.Add(new ComboBoxItem { Content = _text.Auto, Tag = LanguageCatalog.Auto });
        foreach (var info in LanguageCatalog.Languages)
        {
            lang.Items.Add(new ComboBoxItem { Content = info.NativeName, Tag = info.Code });
        }

        lang.SelectedItem = lang.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.Lang)
                            ?? lang.Items[0];
        lang.SelectionChanged += (_, _) =>
        {
            if (lang.SelectedItem is ComboBoxItem { Tag: string code } && code != _settings.Lang)
            {
                _settings.Lang = code;
                _changed();
                _languageChanged();
            }
        };
        _body.Children.Add(lang);

        // Agent
        Section(_text.Agent);
        var agents = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        foreach (var (name, label) in new[] { (AgentKind.Claude, "Claude"), (AgentKind.Codex, "Codex") })
        {
            var button = new ToggleButton { Content = label, IsChecked = _settings.Agent == name, MinWidth = 80 };
            button.Click += (_, _) =>
            {
                if (_settings.Agent != name)
                {
                    _settings.Agent = name;
                    _changed();
                }

                Build();   // each agent shows different options and models
                Refit();
            };
            agents.Children.Add(button);
        }

        _body.Children.Add(agents);

        // Launch options (pin = shown in the ⌄ menu)
        Section(_text.Options);
        foreach (var key in LaunchOptionDefs.All.Where(k => OptionControls.IsShown(_settings, k)))
        {
            var line = new Grid { ColumnSpacing = 10, Margin = new Thickness(0, 2, 0, 2) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.Children.Add(PinButton(key));
            var row = OptionControls.Row(_settings, _text, key, compact: false, _changed);
            Grid.SetColumn(row, 1);
            line.Children.Add(row);
            _body.Children.Add(line);
        }

        // Working folder
        Section(_text.Folder);
        var folderRow = new Grid { ColumnSpacing = 6 };
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        folderRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var folderText = new TextBlock
        {
            Text = CurrentFolder(),
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Opacity = 0.75,
        };
        ToolTipService.SetToolTip(folderText, CurrentFolder());
        folderRow.Children.Add(folderText);
        var change = new Button { Content = _text.FolderChange };
        change.Click += async (_, _) =>
        {
            var picker = new Windows.Storage.Pickers.FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (await picker.PickSingleFolderAsync() is { } picked)
            {
                _settings.Folder = picked.Path;
                _settings.RecentFolders = RecentFolders.Add(_settings.RecentFolders, picked.Path);
                _changed();
                folderText.Text = CurrentFolder();
            }
        };
        Grid.SetColumn(change, 1);
        folderRow.Children.Add(change);
        var home = new Button { Content = _text.FolderHome };
        home.Click += (_, _) =>
        {
            _settings.Folder = "";
            _changed();
            folderText.Text = CurrentFolder();
        };
        Grid.SetColumn(home, 2);
        folderRow.Children.Add(home);
        _body.Children.Add(folderRow);

        // Tab order
        Section(_text.TabOrder);
        var order = BarModes.Normalize(_settings.ModeOrder);
        for (var i = 0; i < order.Count; i++)
        {
            var index = i;
            var line = new Grid { ColumnSpacing = 6, Margin = new Thickness(0, 1, 0, 1) };
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            label.Children.Add(new TextBlock { Text = $"{i + 1}", Opacity = 0.6, Width = 12 });
            label.Children.Add(new FontIcon { Glyph = ModeGlyph(order[i]), FontSize = 14 });
            label.Children.Add(new TextBlock { Text = ModeName(order[i]) });
            line.Children.Add(label);

            var up = OrderButton("\uE70E", i > 0, () => Swap(index, index - 1));      // ▲
            var down = OrderButton("\uE70D", i < order.Count - 1, () => Swap(index, index + 1));   // ▼
            Grid.SetColumn(up, 1);
            Grid.SetColumn(down, 2);
            line.Children.Add(up);
            line.Children.Add(down);

            // On/off (user feedback 09-30: also remove and add Tab-order entries) — disabled modes are skipped by Tab and dimmed. The last one can't be turned off
            var mode = order[i];
            var on = IsModeOn(mode);
            label.Opacity = on ? 1 : 0.45;
            var toggle = new ToggleSwitch { IsOn = on, OnContent = "", OffContent = "", MinWidth = 0, Margin = new Thickness(8, 0, -8, 0) };
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, ModeName(mode));   // unnamed for screen readers before (10-01)
            toggle.Toggled += (_, _) =>
            {
                if (toggle.IsOn == IsModeOn(mode))
                {
                    return;
                }

                if (!toggle.IsOn && order.Count(IsModeOn) <= 1)
                {
                    toggle.IsOn = true;   // one must remain so the input box knows what to do
                    return;
                }

                SetModeOn(mode, toggle.IsOn);
                _changed();
                Build();
                Refit();
            };
            Grid.SetColumn(toggle, 3);
            line.Children.Add(toggle);
            _body.Children.Add(line);
        }

        // Web search engine: auto (per-language default) + this language's engines. A manually chosen engine not in the list (chosen under another language) is shown too
        Section(_text.WebEngine);
        var engines = new ComboBox { MinWidth = 220 };
        var fallback = WebSearch.EnginesFor(_language)[0];
        engines.Items.Add(new ComboBoxItem { Content = $"{_text.Auto} ({fallback.DisplayName})", Tag = WebSearch.Auto });
        var choices = WebSearch.EnginesFor(_language).ToList();
        if (_settings.WebEngine != WebSearch.Auto && choices.All(e => e.Id != _settings.WebEngine))
        {
            choices.Add(WebSearch.Resolve(_settings.WebEngine, _language));
        }

        foreach (var engine in choices)
        {
            engines.Items.Add(new ComboBoxItem { Content = engine.DisplayName, Tag = engine.Id });
        }

        engines.SelectedItem = engines.Items.Cast<ComboBoxItem>().FirstOrDefault(i => (string)i.Tag == _settings.WebEngine)
                               ?? engines.Items[0];
        engines.SelectionChanged += (_, _) =>
        {
            if (engines.SelectedItem is ComboBoxItem { Tag: string id } && id != _settings.WebEngine)
            {
                _settings.WebEngine = id;
                _changed();
            }
        };
        _body.Children.Add(engines);


        // Island notices (09-30 feature): per-kind on/off + calendar (ICS URL)
        Section(_text.NotifySection);
        ToggleRow(_text.NotifyPower, () => _settings.NotifyPower, v => _settings.NotifyPower = v);
        ToggleRow(_text.NotifyBluetooth, () => _settings.NotifyBluetooth, v => _settings.NotifyBluetooth = v);
        ToggleRow(_text.NotifyNetwork, () => _settings.NotifyNetwork, v => _settings.NotifyNetwork = v);
        ToggleRow(_text.NotifyFocus, () => _settings.NotifyFocus, v => _settings.NotifyFocus = v);
        ToggleRow(_text.NotifyClipboard, () => _settings.NotifyClipboard, v => _settings.NotifyClipboard = v);
        ToggleRow(_text.NotifyLoad, () => _settings.NotifyLoad, v => _settings.NotifyLoad = v);
        ToggleRow(_text.NotifyPrivacy, () => _settings.NotifyPrivacy, v => _settings.NotifyPrivacy = v);
        ToggleRow(_text.NotifyToasts, () => _settings.NotifyToasts, v => _settings.NotifyToasts = v);
        if (!Interop.AppPaths.IsPackaged)   // the Store build has no uninstaller that could give the banners back
        {
            ToggleRow(_text.HideToastBanners, () => _settings.HideToastBanners, v => _settings.HideToastBanners = v);   // user 10-01 — a toggle, so it can ship
        }
        TextRow(_text.CalendarIcs, _text.CalendarIcsHint, () => _settings.CalendarIcs, v => _settings.CalendarIcs = v);

        // Timer: Pomodoro lengths + quick timers in the right-click menu (user feedback 09-30: in case they want to change the durations)
        Section(_text.Timer);
        TextRow(_text.PomodoroMinutes, "25 / 5 / 15",
            () => $"{_settings.PomodoroFocus} / {_settings.PomodoroBreak} / {_settings.PomodoroLong}",
            v =>
            {
                var n = ParseMinutes(v);
                if (n.Count == 3)
                {
                    (_settings.PomodoroFocus, _settings.PomodoroBreak, _settings.PomodoroLong) = (n[0], n[1], n[2]);
                }
            });
        TextRow(_text.QuickTimersLabel, "5, 25",
            () => string.Join(", ", _settings.QuickTimers),
            v =>
            {
                var n = ParseMinutes(v);
                if (n.Count > 0)
                {
                    _settings.QuickTimers = [.. n.Distinct().Take(6)];
                }
            });

        // Appearance (preference, 09-30 design C1) — off by default
        Section(_text.StyleSection);
        ToggleRow(_text.StyleGlass, () => _settings.StyleGlass, v => _settings.StyleGlass = v);
        ToggleRow(_text.AudioBars, () => _settings.AudioBars, v => _settings.AudioBars = v);
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "native", "IsleBarTap.dll")))   // left out of a -NoNativeMode Store package (10-03)
        {
            ToggleRow(_text.NativeSearchBox, () => _settings.NativeSearchBox, v => _settings.NativeSearchBox = v);
        }

        // Advanced: power options, grouped under their own heading but always visible (labelled, not hidden). The drop command runs
        // a program on dropped files (we use it for phone transfer); the toggle picks whether a drop attaches to the prompt or runs it (10-01).
        Section(_text.AdvancedSection);
        TextRow(_text.DropCommand, _text.DropCommandHint, () => _settings.DropCommand, v => _settings.DropCommand = v);
        // opt-in crash reports (10-01): stored in the registry next to the setup choice, not in islebar.json
        ToggleRow(_text.CrashReports, () => Interop.CrashReports.Consent, v => Interop.CrashReports.Consent = v);
        ToggleRow(_text.MuteChimeFullscreen, () => _settings.MuteChimeFullscreen, v => _settings.MuteChimeFullscreen = v);   // 10-03
        if (!Interop.AppPaths.IsPackaged)   // the Store updates its own apps
        {
            ToggleRow(_text.CheckUpdates, () => _settings.CheckUpdates, v => _settings.CheckUpdates = v);
        }

        // connect / disconnect Claude Code and Codex without setup (the Store build has none) — 10-01
        // the script takes seconds: the switch is held until it ends and then shows what the files really say — a quick
        // off-on, or a failed script, left it showing "on" with the hooks gone (review 10-03)
        ToggleSwitch? connect = null;
        connect = ToggleRow(_text.ConnectAgents, () => Interop.HookConnector.IsConnected, v =>
        {
            var row = connect!;
            var ui = DispatcherQueue;   // taken here, on the UI thread — not from the pool thread after the window may have closed
            row.IsEnabled = false;
            _ = Task.Run(() => Interop.HookConnector.Set(v)).ContinueWith(_ => ui.TryEnqueue(() =>
            {
                try
                {
                    row.IsOn = Interop.HookConnector.IsConnected;
                    row.IsEnabled = true;
                }
                catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
                {
                    // the window was closed meanwhile
                }
            }), TaskScheduler.Default);
        });
    }

    /// <summary>A row with a name + an on/off toggle on the right.</summary>
    private ToggleSwitch ToggleRow(string label, Func<bool> get, Action<bool> set)
    {
        // label and switch in their own columns: long labels (the crash-report and Claude Code rows in Korean) ran under the
        // switch; now they wrap (seen 10-03)
        var line = new Grid { Margin = new Thickness(0, 1, 0, 1), ColumnSpacing = 12 };
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        line.Children.Add(new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        var toggle = new ToggleSwitch
        {
            IsOn = get(),
            OnContent = "",
            OffContent = "",
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, label);   // unnamed for screen readers before (10-01)
        toggle.Toggled += (_, _) =>
        {
            if (toggle.IsOn != get())
            {
                set(toggle.IsOn);
                _changed();
            }
        };
        Grid.SetColumn(toggle, 1);
        line.Children.Add(toggle);
        _body.Children.Add(line);
        return toggle;
    }

    /// <summary>A name + text input box (saved on leaving the box or pressing Enter).</summary>
    private void TextRow(string label, string hint, Func<string> get, Action<string> set)
    {
        _body.Children.Add(new TextBlock { Text = label, Margin = new Thickness(0, 8, 0, 4) });
        var box = new TextBox { Text = get(), PlaceholderText = hint, TextWrapping = TextWrapping.NoWrap };
        ToolTipService.SetToolTip(box, hint);
        void Commit()
        {
            var value = box.Text.Trim();
            var before = get();
            if (value != before)
            {
                set(value);
                var after = get();
                box.Text = after;   // "30 / 5" or "5, 700" kept only what is valid — the box showed the typed text as if saved (review 10-03)
                if (after != before)
                {
                    _changed();
                }
            }
        }

        box.LostFocus += (_, _) => Commit();
        _commitOnClose.Add(Commit);   // a value typed and then the window closed with X was dropped (review 10-03)
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Commit();
            }
        };
        _body.Children.Add(box);
    }

    /// <summary>Minute numbers (1–600) from text like "25 / 5 / 15" or "5, 25".</summary>
    private static List<int> ParseMinutes(string text)
        => [.. System.Text.RegularExpressions.Regex.Matches(text, @"\d+")
            .Select(m => int.TryParse(m.Value, out var n) ? n : 0)
            .Where(n => n is >= 1 and <= 600)];

    private bool IsModeOn(BarMode mode)
        => mode == BarMode.Files ? _settings.Files : !_settings.ModesOff.Contains(BarModes.Key(mode));

    private void SetModeOn(BarMode mode, bool on)
    {
        if (mode == BarMode.Files)
        {
            _settings.Files = on;   // file search reuses the old switch value (same meaning as toggling Everything)
            return;
        }

        var key = BarModes.Key(mode);
        _settings.ModesOff.Remove(key);
        if (!on)
        {
            _settings.ModesOff.Add(key);
        }
    }

    private void Section(string title)
        => _body.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            // 10 (not 14) above: once the rows within a section were tightened, a 14 dip section gap stood out — the space under
            // the last launch option ("remote control") read as too airy before "working folder" (user feedback 10-01).
            Margin = new Thickness(0, 10, 0, 4),
        });

    private Button OrderButton(string glyph, bool enabled, Action move)
    {
        var button = new Button
        {
            Content = new FontIcon { Glyph = glyph, FontSize = 12 },
            Padding = new Thickness(8, 4, 8, 4),
            IsEnabled = enabled,
        };
        button.Click += (_, _) => move();
        return button;
    }

    /// <summary>Swaps two modes' positions in the Tab order, then saves and redraws.</summary>
    private void Swap(int a, int b)
    {
        var order = BarModes.Normalize(_settings.ModeOrder);
        (order[a], order[b]) = (order[b], order[a]);
        _settings.ModeOrder = [.. order.Select(BarModes.Key)];
        _changed();
        Build();
        Refit();
    }

    private string ModeName(BarMode mode) => mode switch
    {
        BarMode.Files => _text.Files,
        BarMode.Web => _text.WebSearch,
        _ => _settings.Agent == AgentKind.Codex ? "Codex" : "Claude",
    };

    /// <summary>Same icons as the search box (the Claude spark is an image, so the chat glyph stands in here).</summary>
    private static string ModeGlyph(BarMode mode) => mode switch
    {
        BarMode.Files => "\uE8B7",   // folder
        BarMode.Web => "\uE721",     // magnifier
        _ => "\uE8BD",               // chat
    };

    private Button PinButton(string key)
    {
        var icon = new FontIcon { FontSize = 14 };
        var button = new Button
        {
            Content = icon,
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(4),
            VerticalAlignment = VerticalAlignment.Center,
        };

        void Paint()
        {
            var on = _settings.Quick.Contains(key);
            icon.Glyph = on ? "" : "";   // pinned / unpinned
            icon.Foreground = on
                ? (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"]
                : (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"];
        }

        button.Click += (_, _) =>
        {
            if (!_settings.Quick.Remove(key))
            {
                _settings.Quick.Add(key);
                // Sort in settings-screen order (same as the Python version)
                _settings.Quick.Sort((a, b) =>
                    LaunchOptionDefs.All.ToList().IndexOf(a).CompareTo(LaunchOptionDefs.All.ToList().IndexOf(b)));
            }

            Paint();
            _changed();
        };
        Paint();
        return button;
    }

    /// <summary>The current UI language (if auto, the one determined from the Windows language).</summary>
    private string _language => _currentLanguage();

    private string CurrentFolder()
        => string.IsNullOrEmpty(_settings.Folder)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), FolderTrust.DefaultFolderName)
            : _settings.Folder;
}
