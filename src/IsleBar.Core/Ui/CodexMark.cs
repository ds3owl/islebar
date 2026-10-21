namespace IsleBar.Core.Ui;

/// <summary>
/// Finds the Codex mark on the user's own PC — IsleBar ships no OpenAI logo (trademark, user 10-01: no legal risk).
/// codex.exe carries no icon, but OpenAI's Codex extension for VS Code (and its forks) keeps the mark as
/// <c>resources/blossom-black.svg</c> / <c>blossom-white.svg</c>, and OpenAI's ChatGPT app from the Microsoft Store has it
/// as its tile logo. Null when neither is installed → the bar uses Windows glyphs.
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
        Path.Combine(userProfile, ".vscode-oss", "extensions"),
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

    /// <summary>Package name prefix of OpenAI's Microsoft Store apps (the ChatGPT app's package is <c>OpenAI.Codex</c>).</summary>
    public const string StorePackagePrefix = "OpenAI.";

    /// <summary>
    /// The best tile logo of the newest OpenAI Store app among <paramref name="packageRoots"/> (install folders, named by the
    /// package's full name), or null. The manifest names the logos; of the variants next to them an "unplated" one (the bare mark,
    /// filling the image) wins, then the widest.
    /// Never throws.
    /// </summary>
    public static string? FindStoreLogo(IEnumerable<string> packageRoots)
    {
        foreach (var root in packageRoots.OrderByDescending(r => PackageVersionOf(Path.GetFileName(r.TrimEnd('\\', '/')))))
        {
            try
            {
                var manifest = Path.Combine(root, "AppxManifest.xml");
                if (!File.Exists(manifest))
                {
                    continue;
                }

                var best = LogoNames(File.ReadAllText(manifest))
                    .SelectMany(name => Variants(root, name))
                    .Select(path => (Path: path, Width: PngWidth(path)))
                    .Where(c => c.Width > 0)
                    .OrderByDescending(c => c.Path.Contains("unplated", StringComparison.OrdinalIgnoreCase))   // the bare mark, filling the image
                    .ThenByDescending(c => c.Width)
                    .FirstOrDefault();
                if (best.Path is not null)
                {
                    return best.Path;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // try the next package
            }
        }

        return null;
    }

    /// <summary>The logo files a manifest names (<c>Square44x44Logo</c>, <c>Square150x150Logo</c>, store <c>Logo</c>), relative paths.</summary>
    public static IReadOnlyList<string> LogoNames(string manifestXml) =>
        System.Text.RegularExpressions.Regex.Matches(manifestXml,
                @"(?:Square44x44Logo|Square150x150Logo)=""([^""]+\.png)""|<Logo>([^<]+\.png)</Logo>",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Select(m => m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary><c>Assets\Logo.png</c> → every <c>Assets\Logo*.png</c> on disk (scale-200, targetsize-256_altform-unplated, …).</summary>
    private static IEnumerable<string> Variants(string root, string relative)
    {
        var path = Path.Combine(root, relative.Replace('\\', Path.DirectorySeparatorChar));
        var dir = Path.GetDirectoryName(path);
        if (dir is null || !Directory.Exists(dir))
        {
            return [];
        }

        var stem = Path.GetFileNameWithoutExtension(path);
        return Directory.EnumerateFiles(dir, stem + "*.png")
            .Where(f => Path.GetFileName(f).Equals(stem + ".png", StringComparison.OrdinalIgnoreCase)
                || Path.GetFileName(f).StartsWith(stem + ".", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A PNG file's width from its header (0 when it isn't a readable PNG).</summary>
    public static int PngWidth(string path)
    {
        try
        {
            using var file = File.OpenRead(path);
            var head = new byte[24];
            if (file.Read(head, 0, 24) < 24 || head[1] != (byte)'P' || head[2] != (byte)'N' || head[3] != (byte)'G')
            {
                return 0;
            }

            return (head[16] << 24) | (head[17] << 16) | (head[18] << 8) | head[19];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    /// <summary>"OpenAI.Codex_1.2025.300.0_x64__2p2nqsd0c76g0" → 1.2025.300.0 (0.0 when it can't be read).</summary>
    public static Version PackageVersionOf(string fullName)
    {
        var parts = fullName.Split('_');
        return parts.Length > 1 && Version.TryParse(parts[1], out var v) ? v : new Version(0, 0);
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
