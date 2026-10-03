using IsleBar.App.Interop;
using IsleBar.App.Options;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IsleBar.App.Search;

/// <summary>
/// File search results list. Pops up right above the search box (same position and look as the Python version's <c>show_results</c>).
/// <b>Does not take focus</b> (NOACTIVATE) — typing must continue in the input box while the list is shown.
/// Selection (↑↓) and open (Enter) keys are received by the input box and forwarded to this window.
/// </summary>
internal sealed class ResultsWindow : Window
{
    private const int WidthDip = 480;
    private const uint WS_EX_NOACTIVATE = 0x08000000;

    private readonly StackPanel _list = new() { Padding = new Thickness(6), Spacing = 0 };
    private readonly List<Border> _rows = [];
    private IReadOnlyList<FileHit> _hits = [];
    private int _anchorX;
    private int _bandTop;

    public ResultsWindow()
    {
        SystemBackdrop = new DesktopAcrylicBackdrop();
        PopupPlacement.MakeToolPopup(this);
        Content = _list;

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
        var ex = NativeMethods.Unsigned32(NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE));
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE, NativeMethods.Signed32(ex | WS_EX_NOACTIVATE));
    }

    /// <summary>
    /// Whether the mouse is over the list right now. A click on it still takes activation from the bar (NOACTIVATE or not), and
    /// the bar used to end typing on that — closing the list before the click arrived, so clicking a result did nothing (10-03).
    /// </summary>
    public bool UnderCursor() => PopupPlacement.UnderCursor(this);

    /// <summary>Called when a row is clicked (mouse). Argument = row index.</summary>
    public event Action<int>? RowClicked;

    public int Selected { get; private set; }

    public FileHit? SelectedHit => Selected >= 0 && Selected < _hits.Count ? _hits[Selected] : null;

    public bool HasHits => _hits.Count > 0;

    /// <summary>Shows results (or a single hint message). Starts with the first row selected.</summary>
    public void Show(IReadOnlyList<FileHit> hits, string? note, int anchorX, int bandTop)
    {
        (_hits, _anchorX, _bandTop, Selected) = (hits, anchorX, bandTop, 0);
        _list.Children.Clear();
        _rows.Clear();

        for (var i = 0; i < hits.Count; i++)
        {
            _list.Children.Add(Row(hits[i], i));
        }

        if (note is not null)
        {
            _list.Children.Add(new TextBlock
            {
                Text = note,
                Opacity = 0.7,
                Margin = new Thickness(12, 10, 12, 10),
            });
        }

        Paint();
        _list.UpdateLayout();
        PopupPlacement.SizeToContent(this, _list, WidthDip);
        PopupPlacement.PlaceAbove(this, _anchorX, _bandTop);
        AppWindow.Show(activateWindow: false);
    }

    public void Hide() => AppWindow.Hide();

    /// <summary>Moves the selected row. False if the list is empty.</summary>
    public bool Move(int delta)
    {
        if (_hits.Count == 0)
        {
            return false;
        }

        Selected = Math.Clamp(Selected + delta, 0, _hits.Count - 1);
        Paint();
        return true;
    }

    private Border Row(FileHit hit, int index)
    {
        var grid = new Grid { ColumnSpacing = 12, Padding = new Thickness(10, 7, 10, 7) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        grid.Children.Add(new FontIcon
        {
            Glyph = hit.IsFolder ? "" : "",   // folder / document (same glyphs as the Python version)
            FontSize = 16,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var text = new StackPanel();
        text.Children.Add(new TextBlock { Text = hit.Name, TextTrimming = TextTrimming.CharacterEllipsis });
        text.Children.Add(new TextBlock
        {
            Text = hit.Directory,
            FontSize = 11,
            Opacity = 0.65,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        var row = new Border
        {
            Child = grid,
            CornerRadius = new CornerRadius(4),
            MaxWidth = WidthDip - 12,
        };
        row.PointerEntered += (_, _) =>
        {
            Selected = index;
            Paint();
        };
        row.PointerPressed += (_, _) => RowClicked?.Invoke(index);
        _rows.Add(row);
        return row;
    }

    /// <summary>Only the selected row gets a light background + left accent bar (like Windows 11 lists).</summary>
    private void Paint()
    {
        var subtle = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"];
        var accent = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        for (var i = 0; i < _rows.Count; i++)
        {
            var on = i == Selected;
            _rows[i].Background = on ? subtle : clear;
            _rows[i].BorderBrush = on ? accent : clear;
            _rows[i].BorderThickness = on ? new Thickness(3, 0, 0, 0) : new Thickness(0);
        }
    }
}
