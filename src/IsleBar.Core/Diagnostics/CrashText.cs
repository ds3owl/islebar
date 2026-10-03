using System.Text.RegularExpressions;

namespace IsleBar.Core.Diagnostics;

/// <summary>
/// Cleans text before it may leave the PC in a crash report: exception messages can quote paths ("Could not find file
/// 'C:\Users\…'") or names. Paths become &lt;path&gt;, quoted text becomes '…', and long text is cut.
/// </summary>
public static partial class CrashText
{
    public const int MaxLength = 300;

    public static string? Clean(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        var s = PathPattern().Replace(text, "<path>");
        s = UncPattern().Replace(s, "<path>");
        s = QuotedPattern().Replace(s, "'…'");
        return s.Length > MaxLength ? s[..MaxLength] + "…" : s;
    }

    /// <summary>
    /// A bar exit worth reporting: not a normal quit/restart (0, 3, 4, 5), not a managed crash (0xE0434352 — reported with its
    /// exception), and not someone ending it from outside (1 = Task Manager / taskkill /F, -1 = a script killing it, setup).
    /// </summary>
    public static bool IsNativeCrash(int exitCode)
        => exitCode is not (0 or 1 or 3 or 4 or 5 or -1) && unchecked((uint)exitCode) != 0xE0434352;

    // a drive path runs to the end of the line or to a quote: "C:\Users\kim\x.json is denied" → "<path>"
    [GeneratedRegex(@"[A-Za-z]:[\\/][^\r\n'""<>|]*", RegexOptions.CultureInvariant)]
    private static partial Regex PathPattern();

    // UNC: \\server\share\…
    [GeneratedRegex(@"[\\]{2}[^\s'""<>|]+", RegexOptions.CultureInvariant)]
    private static partial Regex UncPattern();

    [GeneratedRegex(@"'[^'\r\n]{2,}'|""[^""\r\n]{2,}""", RegexOptions.CultureInvariant)]
    private static partial Regex QuotedPattern();
}
