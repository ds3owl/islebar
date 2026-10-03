using IsleBar.App.Options;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace IsleBar.App.Search;

/// <summary>
/// Long prompt compose window (user feedback 09-30: the first prompt is usually long, but it's shown only as a short line now, which makes it hard to write).
/// A multi-line input that expands above the search box — Enter sends, Shift+Enter inserts a newline, Esc closes (the draft is kept).
/// The single-line input only takes the first line of a multi-line paste, so pastes like that come here too.
/// </summary>
internal sealed class ComposeWindow : Window
{
    private const int WidthDip = 460;

    private readonly TextBox _box;
    private readonly Action<string> _submit;
    private readonly Action<string> _cancel;
    private bool _done;

    /// <param name="submit">Enter — the text to send.</param>
    /// <param name="cancel">Esc / click elsewhere — the draft (to continue next time).</param>
    /// <param name="take">Text to take the moment the compose window is ready (the input box's latest text — typing continues while the window opens, so taking it clears the input box).</param>
    public ComposeWindow(Func<string> take, string placeholder, Action<string> submit, Action<string> cancel)
    {
        _submit = submit;
        _cancel = cancel;
        SystemBackdrop = new DesktopAcrylicBackdrop();
        _box = new TextBox
        {
            PlaceholderText = placeholder,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 14,
            CharacterSpacing = -11,
            MinHeight = 60,
            BorderThickness = new Thickness(0),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        };
        ScrollViewer.SetVerticalScrollBarVisibility(_box, ScrollBarVisibility.Auto);
        _box.Resources["TextControlBackgroundFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _box.Resources["TextControlBackgroundPointerOver"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _box.Resources["TextControlBorderBrushFocused"] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        var hint = new TextBlock
        {
            Text = "Enter ↵   ·   Shift+Enter ⏎   ·   Esc",
            FontSize = 11,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),   // flush to the panel's right text edge, same as the preview window's hint
        };
        // Fixed width — measuring without a width limit laid text out on one line without wrapping and the window grew off-screen (09-30 PC)
        var panel = new StackPanel { Padding = new Thickness(12, 10, 12, 10), Width = WidthDip };
        panel.Children.Add(_box);
        panel.Children.Add(hint);
        Content = panel;

        _box.AddHandler(UIElement.PreviewKeyDownEvent, new KeyEventHandler(OnKey), handledEventsToo: true);
        _box.TextChanged += (_, _) => Refit();

        // Caret to the end first — characters typed while the window was opening got inserted at the front (09-30 PC: "ingsPlease … sett")
        _box.Loaded += (_, _) =>
        {
            _box.Text = take();
            _box.SelectionStart = _box.Text.Length;
            _box.Focus(FocusState.Programmatic);
        };
        Activated += (_, e) =>
        {
            if (e.WindowActivationState == WindowActivationState.Deactivated)
            {
                Finish(send: false);
            }
        };
        PopupPlacement.MakeToolPopup(this);
        // Chromeless acrylic card (no border, no "WinUI Desktop" caption) — the clean look the user wants for the long-prompt windows (09-30).
        if (AppWindow.Presenter is Microsoft.UI.Windowing.OverlappedPresenter presenter2)
        {
            presenter2.SetBorderAndTitleBar(false, false);
        }
    }

    private int _anchorX;
    private int _bandTop;

    public void ShowAbove(int anchorX, int bandTop)
    {
        (_anchorX, _bandTop) = (anchorX, bandTop);
        // Grow up to the top of the screen, then scroll inside — it used to stop at 10 lines, cutting a long prompt off (user 10-01)
        _box.MaxHeight = PopupPlacement.MaxHeightAbove(anchorX, bandTop) - 32;
        PopupPlacement.ShowSized(this, (FrameworkElement)Content, WidthDip, anchorX, bandTop);
    }

    /// <summary>As lines grow, the window grows upward too (up to the top of the screen, then scrolls inside).</summary>
    private void Refit()
    {
        // Set the box height even before the window's content is loaded: the handed-over text arrives in the box's Loaded, which can
        // run before the panel's, and the first sizing (ShowSized) then uses this height.
        _box.Height = TextHeight();
        if (Content is FrameworkElement content && content.IsLoaded)
        {
            PopupPlacement.SizeToContent(this, content, WidthDip);
            PopupPlacement.PlaceAbove(this, _anchorX, _bandTop);
        }
    }

    /// <summary>
    /// Height the box needs for its text, between its min and max. A multi-line TextBox reports only its minimum when measured, so the
    /// window never grew and a long prompt was cut off after a few lines (measured on PC 10-01: box 964 tall, panel measured 100) —
    /// so measure the text itself at the box's wrapping width.
    /// </summary>
    private double TextHeight()
    {
        var text = _box.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        var probe = new TextBlock
        {
            Text = text.EndsWith('\n') ? text + "\u200B" : text,   // a trailing newline is a line too (the caret sits on it)
            FontSize = _box.FontSize,
            FontFamily = _box.FontFamily,
            CharacterSpacing = _box.CharacterSpacing,
            TextWrapping = TextWrapping.Wrap,
        };
        var width = WidthDip - 24 - _box.Padding.Left - _box.Padding.Right - 4;   // panel padding 12+12, box padding, a little slack for the caret
        probe.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        var height = probe.DesiredSize.Height + _box.Padding.Top + _box.Padding.Bottom + 6;
        return Math.Clamp(height, _box.MinHeight, double.IsFinite(_box.MaxHeight) ? _box.MaxHeight : height);
    }

    private void OnKey(object sender, KeyRoutedEventArgs e)
    {
        var shift = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift)
            .HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case Windows.System.VirtualKey.Enter when !shift:
                e.Handled = true;
                Finish(send: true);
                break;
            case Windows.System.VirtualKey.Escape:
                e.Handled = true;
                Finish(send: false);
                break;
        }
    }

    private void Finish(bool send)
    {
        if (_done)
        {
            return;
        }

        _done = true;
        var text = _box.Text.Replace("\r\n", "\n").Replace('\r', '\n');
        if (send && text.Trim().Length > 0)
        {
            _submit(text.Trim());
        }
        else
        {
            _cancel(text);
        }

        Close();
    }
}
