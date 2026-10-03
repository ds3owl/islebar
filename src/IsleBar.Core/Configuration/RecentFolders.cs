namespace IsleBar.Core.Configuration;

/// <summary>
/// List of recently used working folders.
///
/// <para><b>Why it is needed.</b> Slash commands living in <c>&lt;folder&gt;/.claude/commands</c> are usable
/// <b>only when launched from that folder</b>. With a single folder pinned in settings, using another
/// project's commands means opening settings every time. So we remember the last few and let the bar pick them directly.</para>
/// </summary>
public static class RecentFolders
{
    /// <summary>How many to remember.</summary>
    public const int Max = 8;

    /// <summary>
    /// Puts the folder at the front. If already present it is moved up; overflow is dropped from the end.
    /// Empty values are ignored. Comparison is case-insensitive (Windows rules).
    /// </summary>
    public static List<string> Add(IEnumerable<string>? current, string? folder)
    {
        var list = Normalize(current);
        var f = Trim(folder);
        if (f.Length == 0)
        {
            return list;
        }

        list.RemoveAll(x => Same(x, f));
        list.Insert(0, f);
        if (list.Count > Max)
        {
            list.RemoveRange(Max, list.Count - Max);
        }

        return list;
    }

    /// <summary>Filters out folders that do not exist. The injected function decides which folders are alive.</summary>
    public static List<string> Prune(IEnumerable<string>? current, Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        return [.. Normalize(current).Where(exists)];
    }

    /// <summary>Removes empty values and duplicates and trims to size. Order is kept.</summary>
    public static List<string> Normalize(IEnumerable<string>? current)
    {
        var list = new List<string>();
        foreach (var raw in current ?? [])
        {
            var f = Trim(raw);
            if (f.Length == 0 || list.Any(x => Same(x, f)))
            {
                continue;
            }

            list.Add(f);
            if (list.Count == Max)
            {
                break;
            }
        }

        return list;
    }

    /// <summary>Short name for the list — the last folder name. If names collide, include one level up.</summary>
    public static string ShortName(string folder)
    {
        var f = Trim(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (f.Length == 0)
        {
            return string.Empty;
        }

        var name = Path.GetFileName(f);
        return name.Length > 0 ? name : f;   // roots like "C:\" stay as-is
    }

    private static string Trim(string? value) => (value ?? string.Empty).Trim();

    private static bool Same(string a, string b)
        => string.Equals(
            a.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            b.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
}
