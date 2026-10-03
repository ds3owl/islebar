namespace IsleBar.Core.Launch;

/// <summary>
/// npm installs a CLI on Windows as a <c>.cmd</c> wrapper (<c>codex.cmd</c> → <c>node …\codex.js %*</c>). Starting a .cmd runs it
/// through cmd.exe, which re-reads the arguments: in a prompt, <c>&amp;</c> and <c>|</c> ran other commands, <c>%NAME%</c> was replaced
/// and everything after the first line was dropped (review 10-03). So the wrapper is skipped: node is started with the script
/// directly, and the arguments reach the CLI exactly as typed.
/// </summary>
public static class NpmShim
{
    /// <summary>
    /// For an npm <c>.cmd</c> wrapper, the program and script to run instead (<c>[node, script]</c>); null when <paramref name="path"/>
    /// is not such a wrapper or its target can't be found.
    /// </summary>
    /// <param name="path">The resolved executable (e.g. <c>%APPDATA%\npm\codex.cmd</c>).</param>
    /// <param name="exists">File check.</param>
    /// <param name="readText">Reads a file's text.</param>
    /// <param name="searchPath">PATH, to find node when the wrapper has no node.exe beside it.</param>
    public static IReadOnlyList<string>? Unwrap(string path, Func<string, bool> exists, Func<string, string> readText, string? searchPath)
        => Unwrap(path, exists, readText, searchPath, depth: 0);

    private static IReadOnlyList<string>? Unwrap(string path, Func<string, bool> exists, Func<string, string> readText, string? searchPath, int depth)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(readText);
        if (string.IsNullOrEmpty(path) || !path.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase) || !exists(path))
        {
            return null;
        }

        string text;
        try
        {
            text = readText(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }

        // The script is named relative to the wrapper's folder: npm 7+ writes "%dp0%\…\x.js", npm 6 and pnpm "%~dp0\…\x.js"
        // (pnpm's wrapper may sit beside its own node.exe). Yarn's wrapper just calls another .cmd — followed once.
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        foreach (var marker in new[] { "%dp0%\\", "%~dp0\\" })
        {
            for (var at = text.IndexOf(marker, StringComparison.OrdinalIgnoreCase); at >= 0;
                 at = text.IndexOf(marker, at + 1, StringComparison.OrdinalIgnoreCase))
            {
                var start = at + marker.Length;
                var end = text.IndexOf('"', start);
                if (end <= start)
                {
                    continue;
                }

                var relative = text[start..end];
                if (relative.EndsWith(".js", StringComparison.OrdinalIgnoreCase) || relative.EndsWith(".cjs", StringComparison.OrdinalIgnoreCase)
                    || relative.EndsWith(".mjs", StringComparison.OrdinalIgnoreCase))
                {
                    var script = Path.GetFullPath(Path.Combine(dir, relative));
                    var node = Path.Combine(dir, "node.exe");
                    if (!exists(node))
                    {
                        node = FindOnPath("node.exe", exists, searchPath) ?? string.Empty;
                    }

                    return exists(script) && node.Length > 0 ? [node, script] : null;
                }

                if (depth == 0 && relative.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
                {
                    return Unwrap(Path.GetFullPath(Path.Combine(dir, relative)), exists, readText, searchPath, depth + 1);
                }
            }
        }

        return null;
    }

    private static string? FindOnPath(string fileName, Func<string, bool> exists, string? searchPath)
    {
        foreach (var dir in (searchPath ?? string.Empty).Split(Path.PathSeparator))
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(trimmed, fileName);
                if (exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }
}
