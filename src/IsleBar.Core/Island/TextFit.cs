namespace IsleBar.Core.Island;

/// <summary>The side that measures text width. The app (WinUI) does the real measuring; tests inject a fake.</summary>
public interface ITextMeasure
{
    /// <summary>Width of the text. Any unit works as long as it matches the max width passed to <see cref="TextFit"/>.</summary>
    double Measure(string text);
}

/// <summary>
/// Shortens a name to fit the slot width, ending it with "…". Same behaviour as the Python version's <c>_fit</c>.
/// The prefix (progress / status icon) varies in length by language and progress, so it must be measured too.
/// </summary>
public static class TextFit
{
    public const string Ellipsis = "…";

    /// <summary>
    /// If <paramref name="prefix"/> + <paramref name="name"/> fits in <paramref name="maxWidth"/>
    /// it is returned as-is; otherwise the end of the name is trimmed and "…" appended.
    /// Even if the prefix alone overflows, at least one character of the name + "…" is kept (better than an empty line).
    /// </summary>
    public static string Fit(string prefix, string? name, double maxWidth, ITextMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        prefix ??= string.Empty;
        name ??= string.Empty;

        if (measure.Measure(prefix + name) <= maxWidth)
        {
            return prefix + name;
        }

        // trim by text element so surrogate pairs (emoji etc.) are never split in half
        var elements = TextElements(name);
        while (elements.Count > 1 && measure.Measure(prefix + string.Concat(elements) + Ellipsis) > maxWidth)
        {
            elements.RemoveAt(elements.Count - 1);
        }

        return prefix + string.Concat(elements) + Ellipsis;
    }

    /// <summary>
    /// A <see cref="Fit"/> that returns the prefix (e.g. song title + " · ") and the name (e.g. artist) separately.
    /// If the prefix plus the first character of the name + "…" fits, only the name is shortened. <b>If the prefix alone overflows, the prefix is
    /// shortened to end in "…" and the name is dropped</b> — previously only the name was shortened and long song titles were cut off abruptly at the edge (user feedback 09-30: apply the ellipsis to the song too).
    /// </summary>
    public static (string Head, string Tail) FitParts(string prefix, string? name, double maxWidth, ITextMeasure measure)
    {
        ArgumentNullException.ThrowIfNull(measure);
        prefix ??= string.Empty;
        name ??= string.Empty;
        var first = TextElements(name).FirstOrDefault() ?? string.Empty;
        if (prefix.Length == 0 || measure.Measure(prefix + first + Ellipsis) <= maxWidth)
        {
            var fitted = Fit(prefix, name, maxWidth, measure);
            return (prefix, fitted[prefix.Length..]);
        }

        // trim with joiners like " · " removed
        var head = TextElements(prefix.TrimEnd().TrimEnd('·').TrimEnd());
        while (head.Count > 1 && measure.Measure(string.Concat(head) + Ellipsis) > maxWidth)
        {
            head.RemoveAt(head.Count - 1);
        }

        return (string.Concat(head).TrimEnd() + Ellipsis, string.Empty);
    }

    private static List<string> TextElements(string value)
    {
        var list = new List<string>(value.Length);
        var enumerator = System.Globalization.StringInfo.GetTextElementEnumerator(value);
        while (enumerator.MoveNext())
        {
            list.Add((string)enumerator.Current);
        }

        return list;
    }
}
