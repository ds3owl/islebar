namespace IsleBar.Core.Commands;

/// <summary>Where a command came from. Also the priority when names collide (smaller value wins).</summary>
public enum CommandSource
{
    /// <summary>The working folder's <c>.claude/commands</c>. Only usable when launched from that folder.</summary>
    Project = 0,

    /// <summary><c>~/.claude/commands</c>. Usable wherever it is launched from.</summary>
    User = 1,

    /// <summary>A command brought in by a plugin. Its name is prefixed with the plugin name.</summary>
    Plugin = 2,
}

/// <summary>
/// A single slash command. Typing <c>/</c> in the bar shows this list.
/// </summary>
/// <param name="Name">Name without the leading <c>/</c>. Subfolders are joined with <c>:</c> (<c>git:commit</c>).</param>
/// <param name="Source">Where it came from.</param>
/// <param name="Description">The front matter's <c>description</c>. Empty string if absent.</param>
/// <param name="ArgumentHint">The front matter's <c>argument-hint</c>. Empty string if absent.</param>
/// <param name="Path">Path to the command file.</param>
public sealed record SlashCommand(
    string Name,
    CommandSource Source,
    string Description,
    string ArgumentHint,
    string Path)
{
    /// <summary>The form that can be put into the bar as-is (<c>/name</c>).</summary>
    public string Typed => "/" + Name;

    /// <summary>
    /// One line to show in the list. For commands that take arguments, also shows what the arguments are.
    /// </summary>
    public string Display => ArgumentHint.Length > 0 ? $"{Typed} {ArgumentHint}" : Typed;
}
