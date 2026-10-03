namespace IsleBar.Core.Launch;

/// <summary>
/// Finds the <c>claude</c> executable. PATH comes first; otherwise
/// <c>%USERPROFILE%\.local\bin\claude.exe</c> (where the non-npm install puts it).
/// Only deals with path rules, so it is platform-independent — file checks go through the injected <paramref name="exists"/>.
/// </summary>
public static class ClaudeExecutable
{
    /// <summary>File name to look for on Windows.</summary>
    public const string WindowsFileName = "claude.exe";

    /// <summary>
    /// The path found. If nowhere, returns <c>%USERPROFILE%\.local\bin\claude.exe</c> as-is
    /// (same as the Python version — if launching fails, the user is told then).
    /// </summary>
    /// <param name="searchPath">Value of the PATH environment variable.</param>
    /// <param name="userProfile">User folder (%USERPROFILE%).</param>
    /// <param name="exists">Function that checks whether a file exists.</param>
    /// <param name="fileName">File name to look for.</param>
    public static string Resolve(
        string? searchPath,
        string userProfile,
        Func<string, bool> exists,
        string fileName = WindowsFileName)
    {
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(userProfile);

        foreach (var dir in (searchPath ?? string.Empty).Split(Path.PathSeparator))
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            string candidate;
            try
            {
                candidate = Path.Combine(trimmed, fileName);
            }
            catch (ArgumentException)
            {
                continue;    // entry containing characters not allowed in PATH
            }

            if (exists(candidate))
            {
                return candidate;
            }
        }

        return Path.Combine(userProfile, ".local", "bin", fileName);
    }

    /// <summary>Looks it up in the real environment.</summary>
    public static string Resolve()
        => Resolve(
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            File.Exists,
            OperatingSystem.IsWindows() ? WindowsFileName : "claude");

    /// <summary>
    /// Finds it via the agent profile. PATH first; otherwise the locations the profile gives are
    /// checked <b>in the order listed</b>. If nowhere, returns the first location as-is
    /// (if launching fails, the user is told then — same approach as the Python version).
    /// </summary>
    public static string Resolve(
        IAgentProfile profile,
        string? searchPath,
        string userProfile,
        Func<string, bool> exists,
        bool windows = true)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(exists);
        ArgumentNullException.ThrowIfNull(userProfile);

        IReadOnlyList<string> fileNames = windows ? profile.WindowsFileNames : [profile.Name];

        foreach (var dir in (searchPath ?? string.Empty).Split(Path.PathSeparator))
        {
            var trimmed = dir.Trim().Trim('"');
            if (trimmed.Length == 0)
            {
                continue;
            }

            foreach (var fileName in fileNames)
            {
                string candidate;
                try
                {
                    candidate = Path.Combine(trimmed, fileName);
                }
                catch (ArgumentException)
                {
                    break;   // entry containing characters not allowed in PATH
                }

                if (exists(candidate))
                {
                    return candidate;
                }
            }
        }

        string? first = null;
        foreach (var rel in profile.FallbackRelativePaths)
        {
            foreach (var fileName in fileNames)
            {
                var candidate = Path.Combine(userProfile, rel, fileName);
                first ??= candidate;
                if (exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return first ?? Path.Combine(userProfile, fileNames[0]);
    }

    /// <summary>Finds the agent executable in the real environment.</summary>
    public static string Resolve(IAgentProfile profile)
        => Resolve(
            profile,
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            File.Exists,
            OperatingSystem.IsWindows());
}
