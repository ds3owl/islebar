namespace IsleBar.Core.Localization;

/// <summary>Search bar placeholder text is <b>always English</b> regardless of language (decision 09-29).</summary>
public static class Placeholders
{
    public const string AskClaude = "Ask Claude";
    public const string SearchFiles = "Search files";
    public const string SearchWeb = "Search the web";

    public static string For(BarMode mode) => mode == BarMode.Files ? SearchFiles : AskClaude;

    /// <summary>Placeholder text used when launching with Codex.</summary>
    public const string AskCodex = "Ask Codex";

    /// <summary>Placeholder text for the chosen agent ("Ask Claude" for Claude, "Ask Codex" for Codex).</summary>
    public static string For(BarMode mode, string? agent)
        => mode == BarMode.Files ? SearchFiles
            : mode == BarMode.Web ? SearchWeb
            : agent == Launch.AgentKind.Codex ? AskCodex
            : AskClaude;
}

/// <summary>Search bar mode. Switched with Tab.</summary>
public enum BarMode
{
    Claude,
    Files,

    /// <summary>Web search (opens results in the default browser).</summary>
    Web,
}

/// <summary>
/// Mode order when pressing Tab. Default is Claude → files → web, and it <b>can be changed in settings</b>
/// (user feedback 09-30: wanted to set the order too). The first mode is the default — the bar goes to it when opened and after input is done.
/// Skipped if file search is turned off.
/// </summary>
public static class BarModes
{
    public static readonly IReadOnlyList<BarMode> DefaultOrder = [BarMode.Claude, BarMode.Files, BarMode.Web];

    /// <summary>Name stored in settings.</summary>
    public static string Key(BarMode mode) => mode switch
    {
        BarMode.Files => "files",
        BarMode.Web => "web",
        _ => "claude",
    };

    public static BarMode? Parse(string? key) => key switch
    {
        "claude" => BarMode.Claude,
        "files" => BarMode.Files,
        "web" => BarMode.Web,
        _ => null,
    };

    /// <summary>
    /// Makes a stored order trustworthy: drops unknown names and duplicates, and appends missing modes in default order.
    /// </summary>
    public static List<BarMode> Normalize(IEnumerable<string?>? keys)
    {
        var order = new List<BarMode>();
        foreach (var key in keys ?? [])
        {
            if (Parse(key) is { } mode && !order.Contains(mode))
            {
                order.Add(mode);
            }
        }

        foreach (var mode in DefaultOrder)
        {
            if (!order.Contains(mode))
            {
                order.Add(mode);
            }
        }

        return order;
    }

    /// <summary>The first usable mode in the order (the default mode).</summary>
    public static BarMode First(IReadOnlyList<BarMode> order, bool filesOn)
        => order.First(m => filesOn || m != BarMode.Files);

    /// <summary>The next mode (wraps to the start at the end). Skipped if file search is off.</summary>
    public static BarMode Next(BarMode current, IReadOnlyList<BarMode> order, bool filesOn)
    {
        var usable = order.Where(m => filesOn || m != BarMode.Files).ToList();
        var at = usable.IndexOf(current);
        return usable[(at + 1) % usable.Count];
    }

    /// <summary>Only enabled modes, in order (file search switch + removed-modes list). If all are off, Claude alone.</summary>
    public static List<BarMode> Usable(IReadOnlyList<BarMode> order, bool filesOn, IEnumerable<string>? off)
    {
        var offSet = (off ?? []).Select(Parse).OfType<BarMode>().ToHashSet();
        var usable = order.Where(m => (m != BarMode.Files || filesOn) && !offSet.Contains(m)).ToList();
        return usable.Count > 0 ? usable : [BarMode.Claude];
    }

    /// <summary>Next mode in the default order (compatibility with older callers).</summary>
    public static BarMode Next(BarMode current, bool filesOn) => Next(current, DefaultOrder, filesOn);
}
