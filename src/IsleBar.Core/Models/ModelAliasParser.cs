using System.Text.RegularExpressions;

namespace IsleBar.Core.Models;

/// <summary>
/// Extracts model family aliases from the "Model aliases" table in the official docs (<c>model-config.md</c>).
///
/// Aliases always point to the latest version of their family in Claude Code (e.g. <c>opus</c> → Opus 5.5),
/// so nothing needs fixing when a new version ships. When a new <b>family</b> appears, this update adds a button automatically.
/// </summary>
public static partial class ModelAliasParser
{
    /// <summary>Docs URL.</summary>
    public const string DocumentUrl = "https://code.claude.com/docs/en/model-config.md";

    /// <summary>Without this header you get 403.</summary>
    public const string UserAgent = "Mozilla/5.0 (islebar)";

    /// <summary>Heading used to find the table.</summary>
    public const string SectionHeading = "### Model aliases";

    /// <summary>Minimum trustworthy count. Fewer than this means the docs layout has changed.</summary>
    public const int MinimumTrusted = 3;

    /// <summary>Special values that are not model families.</summary>
    public static readonly IReadOnlySet<string> Special =
        new HashSet<string>(StringComparer.Ordinal) { "default", "best", "opusplan" };

    // aliases in the table are written in bold as **`name`**
    [GeneratedRegex(@"\*\*`([a-z0-9][a-z0-9.\-]*(?:\[[^\]]*\])?)`\*\*", RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    /// <summary>
    /// Extracts the alias list from the docs body.
    /// <c>null</c> if the table is not found or has fewer than <see cref="MinimumTrusted"/> — the buttons are then left as-is.
    /// </summary>
    /// <remarks>
    /// Exclusions: anything containing brackets (context variants like <c>opus[1m]</c> are options, not families),
    /// anything in <see cref="Special"/>, and duplicates.
    /// </remarks>
    public static IReadOnlyList<string>? Parse(string? markdown)
    {
        if (string.IsNullOrEmpty(markdown))
        {
            return null;
        }

        var start = markdown.IndexOf(SectionHeading, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        var body = markdown[(start + SectionHeading.Length)..];
        var end = body.IndexOf("\n### ", StringComparison.Ordinal);
        if (end >= 0)
        {
            body = body[..end];
        }

        var found = new List<string>();
        foreach (var match in AliasPattern().Matches(body).Cast<Match>())
        {
            var alias = match.Groups[1].Value;
            if (alias.Contains('[', StringComparison.Ordinal) || Special.Contains(alias) || found.Contains(alias))
            {
                continue;
            }

            found.Add(alias);
        }

        return found.Count >= MinimumTrusted ? found : null;
    }

    /// <summary>
    /// Merges the current list with the newly found one.
    /// <list type="bullet">
    ///   <item>Items in both keep <b>the current order</b> — so buttons do not shift before the user's eyes.</item>
    ///   <item>New items are appended <b>at the end</b>.</item>
    ///   <item>Items gone from the docs are <b>removed</b>.</item>
    /// </list>
    /// </summary>
    public static IReadOnlyList<string> Merge(IEnumerable<string> current, IEnumerable<string> found)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(found);

        var foundList = new List<string>();
        foreach (var alias in found)
        {
            if (!string.IsNullOrEmpty(alias) && !foundList.Contains(alias))
            {
                foundList.Add(alias);
            }
        }

        var currentList = new List<string>();
        foreach (var alias in current)
        {
            if (!string.IsNullOrEmpty(alias) && !currentList.Contains(alias))
            {
                currentList.Add(alias);
            }
        }

        var merged = new List<string>(foundList.Count);
        merged.AddRange(currentList.Where(foundList.Contains));
        merged.AddRange(foundList.Where(a => !currentList.Contains(a)));
        return merged;
    }
}
