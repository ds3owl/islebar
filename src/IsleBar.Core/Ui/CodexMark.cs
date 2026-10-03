namespace IsleBar.Core.Ui;

/// <summary>
/// Finds the Codex mark on the user's own PC — IsleBar ships no OpenAI logo (trademark, user 10-01: no legal risk).
/// codex.exe carries no icon, but OpenAI's Codex extension for VS Code (and its forks) keeps the mark as
/// <c>resources/blossom-black.svg</c> / <c>blossom-white.svg</c>. Null when none is installed → the bar uses Windows glyphs.
/// </summary>
public static class CodexMark
{
    /// <summary>Extension folder name prefix (publisher.name), e.g. <c>openai.chatgpt-26.5928.31416-win32-x64</c>.</summary>
    public const string ExtensionPrefix = "openai.chatgpt-";

    /// <summary>Where VS Code, VS Code Insiders, Cursor and Windsurf keep extensions, under the user's profile.</summary>
    public static IReadOnlyList<string> DefaultRoots(string userProfile) =>
    [
        Path.Combine(userProfile, ".vscode", "extensions"),
        Path.Combine(userProfile, ".vscode-insiders", "extensions"),
        Path.Combine(userProfile, ".cursor", "extensions"),
        Path.Combine(userProfile, ".windsurf", "extensions"),
    ];

    /// <summary>
    /// The mark for the theme (black on a light pill, white on a dark one) from the newest installed extension,
    /// or null. Never throws — a locked or vanished folder just means "not found".
    /// </summary>
    public static string? Find(IEnumerable<string> roots, bool light)
    {
        var file = light ? "blossom-black.svg" : "blossom-white.svg";
        var candidates = new List<(Version Version, string Path)>();
        foreach (var root in roots)
        {
            try
            {
                if (!Directory.Exists(root))
                {
                    continue;
                }

                foreach (var dir in Directory.EnumerateDirectories(root, ExtensionPrefix + "*"))
                {
                    var svg = Path.Combine(dir, "resources", file);
                    if (File.Exists(svg))
                    {
                        candidates.Add((VersionOf(Path.GetFileName(dir)), svg));
                    }
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try the next root
            }
        }

        return candidates.OrderByDescending(c => c.Version).Select(c => c.Path).FirstOrDefault();
    }

    /// <summary>"openai.chatgpt-26.5928.31416-win32-x64" → 26.5928.31416 (0.0 when it can't be read).</summary>
    public static Version VersionOf(string folderName)
    {
        var rest = folderName.StartsWith(ExtensionPrefix, StringComparison.OrdinalIgnoreCase) ? folderName[ExtensionPrefix.Length..] : folderName;
        var dash = rest.IndexOf('-', StringComparison.Ordinal);
        var text = dash >= 0 ? rest[..dash] : rest;
        return Version.TryParse(text, out var v) ? v : new Version(0, 0);
    }
}
