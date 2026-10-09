using System.Text;

namespace IsleBar.Core.Launch;

/// <summary>
/// Builds a Windows command line by hand, for the one place that calls CreateProcess directly (the Store build's supervisor,
/// which needs a process attribute <see cref="System.Diagnostics.Process"/> can't pass). Same rules the C runtime and .NET use to
/// split it back, so every argument arrives exactly as given.
/// </summary>
public static class CommandLine
{
    /// <summary>Appends one argument, quoted only when it has to be.</summary>
    public static void Append(StringBuilder line, string arg)
    {
        ArgumentNullException.ThrowIfNull(line);
        ArgumentNullException.ThrowIfNull(arg);
        if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '\n', '\v', '"']) < 0)
        {
            line.Append(arg);
            return;
        }

        line.Append('"');
        var backslashes = 0;
        foreach (var c in arg)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            line.Append('\\', c == '"' ? backslashes * 2 + 1 : backslashes);
            backslashes = 0;
            line.Append(c);
        }

        line.Append('\\', backslashes * 2);
        line.Append('"');
    }

    /// <summary>The whole command line for <paramref name="args"/>, separated by single spaces.</summary>
    public static string Join(IEnumerable<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        var line = new StringBuilder();
        foreach (var arg in args)
        {
            if (line.Length > 0)
            {
                line.Append(' ');
            }

            Append(line, arg);
        }

        return line.ToString();
    }
}
