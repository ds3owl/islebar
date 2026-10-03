using IsleBar.Core.Island;
using Microsoft.UI.Xaml.Controls;

namespace IsleBar.App.IslandUi;

/// <summary>
/// Implements Core's <see cref="ITextMeasure"/> with the real font.
/// Core knows only this interface, so the name-truncation logic can be tested on Linux too.
/// </summary>
/// <param name="bold">Font for the bold part (Pretendard, a separate file per weight) — if absent, treated as text without bold (music) and measured at regular weight.</param>
/// <remarks>
/// Measures exactly as drawn: the part before the first " · " (track title, "42%", etc.) in the bold font, the rest regular — same rule as SetStyledText.
/// Measuring everything as bold (wider) truncated even artist names that would fit with "…" (measured on PC 09-30: "Bruno M…").
/// </remarks>
internal sealed class TextBlockMeasure(TextBlock template, Microsoft.UI.Xaml.Media.FontFamily? bold = null) : ITextMeasure
{
    private const string Separator = " · ";

    private readonly TextBlock _probe = new() { TextWrapping = Microsoft.UI.Xaml.TextWrapping.NoWrap };

    // one probe and two runs reused for every measurement — the bar keeps one of these, and fitting a name measures many
    // times per draw, 4 draws a second: new ones each time piled up native text objects (review 10-03)
    private readonly Microsoft.UI.Xaml.Documents.Run _main = new();
    private readonly Microsoft.UI.Xaml.Documents.Run _rest = new();

    public double Measure(string text)
    {
        // the template's font can change (language switch) — follow it
        _probe.FontFamily = template.FontFamily;
        _probe.FontSize = template.FontSize;
        _probe.CharacterSpacing = template.CharacterSpacing;
        _main.FontFamily = bold ?? template.FontFamily;
        _main.FontWeight = bold is null ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.Medium;

        var dot = text.IndexOf(Separator, StringComparison.Ordinal);
        _main.Text = dot >= 0 ? text[..dot] : text;
        if (_probe.Inlines.Count == 0)
        {
            _probe.Inlines.Add(_main);
        }

        if (dot >= 0)
        {
            _rest.Text = text[dot..];
            if (_probe.Inlines.Count == 1)
            {
                _probe.Inlines.Add(_rest);
            }
        }
        else if (_probe.Inlines.Count == 2)
        {
            _probe.Inlines.RemoveAt(1);
        }

        _probe.InvalidateMeasure();   // the same size is asked each time — make sure the changed text is laid out again
        _probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return _probe.DesiredSize.Width;
    }
}
