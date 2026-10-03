using IsleBar.Core.Configuration;

namespace IsleBar.Core.Launch;

/// <summary>Agent name. Stored in the settings file as-is.</summary>
public static class AgentKind
{
    public const string Claude = "claude";
    public const string Codex = "codex";

    /// <summary>Everything selectable in settings (order = settings screen order).</summary>
    public static readonly IReadOnlyList<string> All = [Claude, Codex];

    public static bool IsKnown(string? value) => value is Claude or Codex;
}

/// <summary>
/// Represents one kind of agent to launch. Executable name, argument building and model list
/// differ per agent, so they are grouped here.
///
/// Options (permissions, session, model, effort, remote control) <b>may have no equivalent for some agents.</b>
/// For such options <see cref="Supports"/> returns false and they are not added to the arguments.
/// The settings screen uses this to hide those items.
/// </summary>
public interface IAgentProfile
{
    /// <summary>The <see cref="AgentKind"/> value.</summary>
    string Name { get; }

    /// <summary>Executable file name to look for on Windows.</summary>
    string WindowsFileName { get; }

    /// <summary>
    /// Every file name it can have on Windows, the usual one first — an npm install is a <c>.cmd</c>, the native one an
    /// <c>.exe</c> (an npm-installed Claude Code was never found — review 10-03).
    /// </summary>
    IReadOnlyList<string> WindowsFileNames => [WindowsFileName];

    /// <summary>
    /// The last place to look when the executable is not found on PATH.
    /// A relative path appended to <c>%USERPROFILE%</c>.
    /// </summary>
    IReadOnlyList<string> FallbackRelativePaths { get; }

    /// <summary>Default used when the settings have no model alias list.</summary>
    IReadOnlyList<string> DefaultModels { get; }

    /// <summary>
    /// Whether model aliases can be fetched automatically from docs.
    /// Claude parses the model docs to update them, but Codex has no such doc and uses a fixed list.
    /// </summary>
    bool SupportsModelAutoUpdate { get; }

    /// <summary>Whether this agent uses the given option name from <see cref="LaunchOptionDefs"/>.</summary>
    bool Supports(string option);

    /// <summary>
    /// Builds the argument list. The first element is <paramref name="executablePath"/> as-is.
    /// </summary>
    IReadOnlyList<string> BuildArgs(string executablePath, string? question, LaunchOptions options);
}
