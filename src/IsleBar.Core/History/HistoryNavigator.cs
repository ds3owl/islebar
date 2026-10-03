namespace IsleBar.Core.History;

/// <summary>
/// Browses previous questions with ↑/↓ in Claude mode. Same behaviour as the Python version's <c>key_updown</c>.
///
/// Key point: <b>text being typed is never lost</b>. On the first ↑ the current input is stashed,
/// and it is handed back when ↓ returns all the way to the bottom.
/// </summary>
public sealed class HistoryNavigator(IReadOnlyList<string> history)
{
    private readonly IReadOnlyList<string> _history = history ?? throw new ArgumentNullException(nameof(history));

    /// <summary>Current browsing position. null = still typing a new entry.</summary>
    public int? Position { get; private set; }

    /// <summary>The text being typed when ↑ was first pressed.</summary>
    public string Draft { get; private set; } = string.Empty;

    /// <summary>Whether we are currently browsing.</summary>
    public bool IsBrowsing => Position is not null;

    /// <summary>
    /// Typing or switching modes forgets the position — so the next ↑ starts from the bottom again.
    /// </summary>
    public void Reset()
    {
        Position = null;
        Draft = string.Empty;
    }

    /// <summary>One step up (older question). Returns the text for the input box. Null if there is no history.</summary>
    public string? Up(string? currentText) => Move(-1, currentText);

    /// <summary>One step down (newer question → the text being typed). Returns the text for the input box. Null if there is no history.</summary>
    public string? Down(string? currentText) => Move(+1, currentText);

    /// <summary>
    /// Moves by <paramref name="delta"/> (-1 = up/older, +1 = down/newer).
    /// Returns the text for the input box. Null if the history is empty (does nothing).
    /// </summary>
    public string? Move(int delta, string? currentText)
    {
        if (_history.Count == 0)
        {
            return null;
        }

        if (Position is null)
        {
            // start at the bottom (= one past the history) and stash the text being typed
            Position = _history.Count;
            Draft = currentText ?? string.Empty;
        }

        Position = Math.Clamp(Position.Value + delta, 0, _history.Count);
        return Position.Value < _history.Count ? _history[Position.Value] : Draft;
    }
}
