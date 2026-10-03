namespace IsleBar.Core.Commands;

/// <summary>
/// Collects the slash commands available on disk.
///
/// <para>Looks in three places.
/// ① <c>&lt;working folder&gt;/.claude/commands</c> — usable <b>only when launched from that folder</b>
/// ② <c>&lt;home&gt;/.claude/commands</c> — usable wherever it is launched from
/// ③ <c>&lt;home&gt;/.claude/plugins/**/commands</c> — names are prefixed with the plugin name</para>
///
/// <para><b>Why the bar reads this.</b> Project commands simply do not exist unless the working folder
/// is that project. Instead of typing one and only then getting "no such command",
/// we show <b>only what is actually usable in this folder right now</b> the moment <c>/</c> is typed.</para>
/// </summary>
public static class SlashCommandScanner
{
    /// <summary>Command file extension.</summary>
    public const string Extension = ".md";

    private const string ClaudeDir = ".claude";
    private const string CommandsDir = "commands";
    private const string PluginsDir = "plugins";

    /// <summary>
    /// Collects the usable commands. When names collide, the one with the smaller
    /// <see cref="CommandSource"/> wins (project → user → plugin).
    /// The result is sorted by name ascending.
    /// </summary>
    /// <param name="workingFolder">Working folder. If empty, project commands are skipped.</param>
    /// <param name="homeFolder">Home folder.</param>
    public static IReadOnlyList<SlashCommand> Scan(string? workingFolder, string? homeFolder)
    {
        var found = new Dictionary<string, SlashCommand>(StringComparer.OrdinalIgnoreCase);

        void Take(SlashCommand cmd)
        {
            // first one added wins — so they are called below in priority order
            if (!found.ContainsKey(cmd.Name))
            {
                found[cmd.Name] = cmd;
            }
        }

        if (!string.IsNullOrWhiteSpace(workingFolder))
        {
            foreach (var c in FromDirectory(Path.Combine(workingFolder, ClaudeDir, CommandsDir), CommandSource.Project, prefix: ""))
            {
                Take(c);
            }
        }

        if (!string.IsNullOrWhiteSpace(homeFolder))
        {
            foreach (var c in FromDirectory(Path.Combine(homeFolder, ClaudeDir, CommandsDir), CommandSource.User, prefix: ""))
            {
                Take(c);
            }

            foreach (var c in FromPlugins(Path.Combine(homeFolder, ClaudeDir, PluginsDir)))
            {
                Take(c);
            }
        }

        var list = found.Values.ToList();
        list.Sort(static (a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return list;
    }

    /// <summary>Scans every <c>*/commands</c> under the plugins folder. Names are <c>plugin:command</c>.</summary>
    private static IEnumerable<SlashCommand> FromPlugins(string pluginsRoot)
    {
        if (!SafeDirectoryExists(pluginsRoot))
        {
            yield break;
        }

        foreach (var commandsDir in SafeEnumerateDirectories(pluginsRoot, CommandsDir))
        {
            // .../plugins/<somewhere>/<plugin>/commands → the plugin name is the folder right above
            var pluginName = Path.GetFileName(Path.GetDirectoryName(commandsDir) ?? string.Empty);
            if (pluginName.Length == 0)
            {
                continue;
            }

            foreach (var c in FromDirectory(commandsDir, CommandSource.Plugin, prefix: pluginName + ":"))
            {
                yield return c;
            }
        }
    }

    /// <summary>Command files in one folder (including subfolders).</summary>
    private static IEnumerable<SlashCommand> FromDirectory(string root, CommandSource source, string prefix)
    {
        if (!SafeDirectoryExists(root))
        {
            yield break;
        }

        foreach (var file in SafeEnumerateFiles(root, "*" + Extension))
        {
            var name = NameFor(root, file, prefix);
            if (name.Length == 0)
            {
                continue;
            }

            var (description, hint) = ReadFrontMatter(file);
            yield return new SlashCommand(name, source, description, hint, file);
        }
    }

    /// <summary>
    /// File path → command name. Takes the path relative to the root, drops the extension,
    /// and turns folder separators into <c>:</c> (<c>git/commit.md</c> → <c>git:commit</c>).
    /// </summary>
    internal static string NameFor(string root, string file, string prefix)
    {
        var rel = Path.GetRelativePath(root, file);
        if (rel.EndsWith(Extension, StringComparison.OrdinalIgnoreCase))
        {
            rel = rel[..^Extension.Length];
        }

        var name = rel.Replace(Path.DirectorySeparatorChar, ':').Replace(Path.AltDirectorySeparatorChar, ':');
        return name.Length == 0 ? string.Empty : prefix + name;
    }

    /// <summary>
    /// Extracts only <c>description</c> and <c>argument-hint</c> from the <c>---</c> front matter at the top of the file.
    /// If there is no front matter or it cannot be read, empty values — <b>the command is not dropped from the list</b> (the name is still usable).
    /// </summary>
    internal static (string Description, string ArgumentHint) ReadFrontMatter(string file)
    {
        string[] lines;
        try
        {
            lines = File.ReadLines(file).Take(40).ToArray();
        }
        catch (IOException)
        {
            return (string.Empty, string.Empty);
        }
        catch (UnauthorizedAccessException)
        {
            return (string.Empty, string.Empty);
        }

        if (lines.Length == 0 || lines[0].Trim() != "---")
        {
            return (string.Empty, string.Empty);
        }

        var description = string.Empty;
        var hint = string.Empty;

        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            if (line.Trim() == "---")
            {
                break;
            }

            var colon = line.IndexOf(':');
            if (colon <= 0)
            {
                continue;
            }

            var key = line[..colon].Trim();
            var value = Unquote(line[(colon + 1)..].Trim());

            if (key.Equals("description", StringComparison.OrdinalIgnoreCase))
            {
                description = value;
            }
            else if (key.Equals("argument-hint", StringComparison.OrdinalIgnoreCase))
            {
                hint = value;
            }
        }

        return (description, hint);
    }

    private static string Unquote(string value)
        => value.Length >= 2 && ((value[0] == '"' && value[^1] == '"') || (value[0] == '\'' && value[^1] == '\''))
            ? value[1..^1]
            : value;

    // ---------- All file access fails silently (the bar must not die on a folder without permission) ----------

    private static bool SafeDirectoryExists(string path)
    {
        try { return Directory.Exists(path); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    private static IEnumerable<string> SafeEnumerateFiles(string root, string pattern)
    {
        try { return Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).ToList(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }

    private static IEnumerable<string> SafeEnumerateDirectories(string root, string name)
    {
        try { return Directory.EnumerateDirectories(root, name, SearchOption.AllDirectories).ToList(); }
        catch (IOException) { return []; }
        catch (UnauthorizedAccessException) { return []; }
    }
}
