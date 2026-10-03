namespace IsleBar.Core.Commands;

/// <summary>
/// Picks the commands matching what was typed. The bar calls this whenever the input starts with <c>/</c>.
/// </summary>
public static class SlashCommandMatcher
{
    /// <summary>Maximum number of items shown in the list at once.</summary>
    public const int MaxResults = 8;

    /// <summary>Whether the input is in command position — i.e. starts with <c>/</c>.</summary>
    public static bool IsCommandInput(string? text)
        => (text ?? string.Empty).TrimStart().StartsWith('/');

    /// <summary>
    /// Just the command-name part of the input. <c>/loop 5m /foo</c> gives <c>loop</c>.
    /// Null if the input is not in command position.
    /// </summary>
    public static string? NamePart(string? text)
    {
        var t = (text ?? string.Empty).TrimStart();
        if (!t.StartsWith('/'))
        {
            return null;
        }

        var rest = t[1..];
        var space = rest.IndexOf(' ');
        return space < 0 ? rest : rest[..space];
    }

    /// <summary>
    /// Whether arguments are already being typed after the name. If so, it is better to close the list.
    /// </summary>
    public static bool HasArguments(string? text)
    {
        var t = (text ?? string.Empty).TrimStart();
        return t.StartsWith('/') && t[1..].Contains(' ');
    }

    /// <summary>
    /// The matches. Prefix matches first, then names containing the text, then descriptions containing it.
    /// Ties are ordered by name ascending. Up to <paramref name="limit"/> items.
    /// </summary>
    public static IReadOnlyList<SlashCommand> Match(
        IReadOnlyList<SlashCommand> commands, string? text, int limit = MaxResults)
    {
        ArgumentNullException.ThrowIfNull(commands);

        var name = NamePart(text);
        if (name is null)
        {
            return [];
        }

        // While typing arguments, only the one exact name match (to show what is being typed)
        if (HasArguments(text))
        {
            var exact = commands.FirstOrDefault(c => c.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
            return exact is null ? [] : [exact];
        }

        if (name.Length == 0)
        {
            return [.. commands.Take(limit)];   // only '/' typed → take from the start
        }

        var ranked = new List<(int Rank, SlashCommand Cmd)>();
        foreach (var c in commands)
        {
            int rank;
            if (c.Name.StartsWith(name, StringComparison.OrdinalIgnoreCase))
            {
                rank = 0;
            }
            else if (c.Name.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                rank = 1;
            }
            else if (c.Description.Contains(name, StringComparison.OrdinalIgnoreCase))
            {
                rank = 2;
            }
            else
            {
                continue;
            }

            ranked.Add((rank, c));
        }

        ranked.Sort(static (a, b) =>
        {
            var byRank = a.Rank.CompareTo(b.Rank);
            return byRank != 0 ? byRank : string.Compare(a.Cmd.Name, b.Cmd.Name, StringComparison.OrdinalIgnoreCase);
        });

        return [.. ranked.Take(limit).Select(static x => x.Cmd)];
    }

    /// <summary>
    /// The text to put in the input box when an item is picked from the list.
    /// For commands that take arguments, a trailing space is added so arguments can be typed right away.
    /// </summary>
    public static string Complete(SlashCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.ArgumentHint.Length > 0 ? command.Typed + " " : command.Typed;
    }
}
