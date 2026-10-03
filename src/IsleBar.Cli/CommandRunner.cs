using System.Globalization;
using IsleBar.Core.Configuration;
using IsleBar.Core.Commands;
using IsleBar.Core.Island;
using IsleBar.Core.Launch;
using IsleBar.Core.Models;

namespace IsleBar.Cli;

/// <summary>
/// The <c>islebar</c> command. The channel other programs use to push state onto the island (integration rule).
/// Runs as-is on Linux too, so it can be tested here.
/// </summary>
public static class CommandRunner
{
    public const int ExitOk = 0;
    public const int ExitUsage = 2;
    public const int ExitFailed = 1;

    public static int Run(string[] args, TextWriter output, TextWriter error)
        => Run(args, output, error, null);

    /// <param name="input">Standard input (where Claude hooks pass JSON). Console.In if null.</param>
    public static int Run(string[] args, TextWriter output, TextWriter error, TextReader? input)
    {
        ArgumentNullException.ThrowIfNull(args);

        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            PrintUsage(output);
            return args.Length == 0 ? ExitUsage : ExitOk;
        }

        try
        {
            return args[0] switch
            {
                "push" => Push(args[1..], output, error),
                "list" => List(args[1..], output, error),
                "show" => Show(args[1..], output, error),
                "rm" => Remove(args[1..], output, error),
                "timer" => Timer(args[1..], output, error),
                "args" => ShowArgs(args[1..], output, error),
                "aliases" => Aliases(args[1..], output, error),
                "commands" => Commands(args[1..], output, error),
                "hook" => Hook(args[1..], input ?? Utf8StandardInput()),
                "statusline" => StatusLine(args[1..], input ?? Utf8StandardInput(), output),
                _ => Unknown(args[0], error),
            };
        }
        catch (ArgError ex)
        {
            error.WriteLine("islebar: " + ex.Message);
            return ExitUsage;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error.WriteLine("islebar: " + ex.Message);
            return ExitFailed;
        }
        catch (ArgumentException ex)   // e.g. --id or --dir given without a value (a raw stack trace before — review 10-03)
        {
            error.WriteLine("islebar: " + ex.Message);
            return ExitUsage;
        }
    }

    /// <summary>
    /// Standard input read as UTF-8. Claude Code sends hook JSON as UTF-8, but Console.In decodes with the console code page
    /// (CP949 on Korean Windows): a Korean character right before an escape like \n swallowed the backslash, the JSON no longer
    /// parsed, and the hook quietly did nothing — the "done" state never got written for answers like that (found on PC 10-01).
    /// </summary>
    private static TextReader Utf8StandardInput()
        => new StreamReader(Console.OpenStandardInput(), new System.Text.UTF8Encoding(false));

    private static int Unknown(string command, TextWriter error)
    {
        error.WriteLine($"islebar: unknown command '{command}'. See --help.");
        return ExitUsage;
    }

    // ---------------- push ----------------

    private static int Push(string[] args, TextWriter output, TextWriter error)
    {
        var opts = ArgMap.Parse(args);
        var kindText = opts.Value("kind") ?? ActivityState.KindTransfer;
        if (ActivityState.ParseKind(kindText) is null)
        {
            throw new ArgError($"--kind must be one of {string.Join(" · ", ActivityState.AllKinds)} (got: {kindText}).");
        }

        var agent = opts.Value("agent");
        if (agent is not null && !AgentKind.IsKnown(agent))
        {
            throw new ArgError($"--agent must be one of {string.Join(" · ", AgentKind.All)} (got: {agent}).");
        }

        var state = new ActivityState
        {
            RawKind = kindText,
            Agent = agent,
            Title = opts.Value("title"),
            Name = opts.Value("name"),
            Stage = opts.Value("stage"),
            Msg = opts.Value("msg"),
            Open = opts.Value("open"),
            State = opts.Value("state") ?? "run",
            Total = opts.Long("total"),
            Done = opts.Long("done"),
            T0 = opts.Double("t0") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            Due = opts.Double("due"),
        };

        if (state.State is not ("run" or "done" or "error"))
        {
            throw new ArgError($"--state must be one of run · done · error (got: {state.State}).");
        }

        var id = opts.Value("id") ?? $"islebar_{Environment.ProcessId}";
        var store = StoreFor(opts);
        var path = store.Write(id, state);
        opts.WarnUnknown(error);
        output.WriteLine(path);
        return ExitOk;
    }

    // ---------------- statusline ----------------

    /// <summary>
    /// Claude Code's status line command (installed by hooks.ps1 when none is set — 10-03). Claude passes its plan usage here
    /// (and nowhere else), so this is where the bar learns "5-hour window at 90%": the first time a window passes it, a short
    /// notice goes up. Prints a compact "5h 42% | 7d 17%" for the status line itself. Never fails — Claude would show the error.
    /// </summary>
    private static int StatusLine(string[] args, TextReader input, TextWriter output)
    {
        try
        {
            var opts = ArgMap.Parse(args);
            var windows = AgentUsage.FromClaudeStatus(input.ReadToEnd());
            if (AgentUsage.ToWarnAbout(windows) is { } high)
            {
                var warned = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsleBar", "usage_warned.txt");
                if (AgentUsage.FirstWarning(warned, AgentKind.Claude, high))
                {
                    var now = DateTimeOffset.UtcNow;
                    var notice = AgentNotices.UsageHigh(AgentKind.Claude, high.Percent, high.ResetsAt, BarLanguage, now);
                    notice.Due = now.AddSeconds(12).ToUnixTimeMilliseconds() / 1000.0;   // a heads-up, not a standing item
                    StoreFor(opts).Write("claude_usage", notice);
                }
            }

            output.Write(string.Join(" | ", windows.Select(w =>   // ASCII only: the console code page would mangle "·"
                $"{w.Key} {Math.Floor(w.Percent).ToString(System.Globalization.CultureInfo.InvariantCulture)}%")));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // pass silently — whatever it is, a crash here would show the user a hook error on every turn (review 10-03)
        }

        return ExitOk;
    }

    // ---------------- hook ----------------

    /// <summary>
    /// The bar's own language (its settings, or Windows' language for "auto"), for text the hook writes itself — the usage-limit
    /// notice. English if the settings can't be read. Read lazily: most hook events need no text.
    /// </summary>
    private static IsleBar.Core.Localization.LanguageStrings BarLanguage
    {
        get
        {
            try
            {
                var settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "IsleBar", "islebar.json");
                var lang = File.Exists(settings) ? new IsleBar.Core.Configuration.ConfigStore(settings).Load().Lang : "auto";
                return IsleBar.Core.Localization.LanguageCatalog.For(
                    IsleBar.Core.Localization.LanguageResolver.Resolve(lang, GetUserDefaultUILanguage()));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException
                                           or System.Text.Json.JsonException or TimeoutException)
            {
                return IsleBar.Core.Localization.LanguageCatalog.For(IsleBar.Core.Localization.LanguageCatalog.Fallback);
            }
        }
    }

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern ushort GetUserDefaultUILanguage();

    /// <summary>
    /// Agent notification → island. Claude Code hooks pass JSON on stdin; Codex <c>notify</c> passes JSON as the last argument.
    /// <b>Never blocks the agent</b>: whatever goes wrong, it exits quietly with 0 (no output either — Claude reads hook output).
    /// </summary>
    private static int Hook(string[] args, TextReader input)
    {
        try
        {
            var opts = ArgMap.Parse(args);
            var agent = opts.Value("agent") ?? AgentKind.Claude;
            // Codex: the legacy notify passes its JSON as the last argument; lifecycle hooks pass it on stdin like Claude's
            var action = agent == AgentKind.Codex
                ? (opts.Rest.Count > 0 ? AgentHook.FromCodex(opts.Rest[^1]) : AgentHook.FromCodexHook(input.ReadToEnd()))
                : AgentHook.FromClaude(input.ReadToEnd(), Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR"), SessionTitle.FromTranscript,
                    BarLanguage);
            if (action is null)
            {
                return ExitOk;
            }

            var store = StoreFor(opts);
            if (action.OnlyOver is { } required)
            {
                if (store.ReadById(action.Id) is not { } current || (current.Kind != required && current.Kind != action.OrOver))
                {
                    return ExitOk;   // e.g. an ordinary tool call — only a pending permission prompt is replaced
                }

                if (action.Write is { } replacing)
                {
                    replacing.Name ??= current.Name;   // keep the session name (not re-read from the transcript on every tool call)
                }
            }

            if (action.Write is { } state)
            {
                if (state.Name is null && action.FallbackName is not null)
                {
                    state.Name = store.ReadById(action.Id)?.Name ?? action.FallbackName;   // Codex: keep the prompt name across events
                }

                if (agent == AgentKind.Codex && state.Transcript is null)
                {
                    state.Transcript = store.ReadById(action.Id)?.Transcript;   // the bar watches it for a failed turn (10-03)
                }
                // Claude Code installed with npm runs as node.exe (the native installer's is claude.exe); without its pid a closed
                // terminal never cleared the pill (review 10-03). The first such ancestor is the agent: hooks run in a shell under it.
                state.Pid = agent == AgentKind.Codex
                    ? AgentProcess.FindAncestor("codex.exe")
                    : AgentProcess.FindAncestor("claude.exe", "node.exe", "bun.exe");
                store.Write(action.Id, state);
            }
            else
            {
                store.Remove(action.Id);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // pass silently — whatever it is, a crash here would show the user a hook error on every turn (review 10-03)
        }

        return ExitOk;
    }

    // ---------------- list / show ----------------

    private static int List(string[] args, TextWriter output, TextWriter error)
    {
        var opts = ArgMap.Parse(args);
        var store = StoreFor(opts);
        opts.WarnUnknown(error);
        var live = store.Read(DateTimeOffset.UtcNow);
        if (live.Count == 0)
        {
            output.WriteLine("(no live states)");
            return ExitOk;
        }

        foreach (var state in live)
        {
            output.WriteLine($"{state.Kind,-17} {state.State,-5} {Percent(state),6}  {state.Name}  [{Path.GetFileName(state.SourcePath)}]");
        }

        return ExitOk;
    }

    private static int Show(string[] args, TextWriter output, TextWriter error)
    {
        var opts = ArgMap.Parse(args);
        var store = StoreFor(opts);
        opts.WarnUnknown(error);
        var snapshot = IslandSelector.Select(store.Read(DateTimeOffset.UtcNow));
        if (!snapshot.IsActive)
        {
            output.WriteLine("(island is empty)");
            return ExitOk;
        }

        var primary = snapshot.Primary!;
        var more = snapshot.HasMore ? $"  (+{snapshot.ExtraCount} more)" : string.Empty;
        output.WriteLine($"{primary.Kind} {primary.State} {Percent(primary)} {primary.Name}{more}");
        return ExitOk;
    }

    private static string Percent(ActivityState state)
        => state.Fraction is { } f ? (f * 100).ToString("0", CultureInfo.InvariantCulture) + "%" : "-";

    private static int Remove(string[] args, TextWriter output, TextWriter error)
    {
        var opts = ArgMap.Parse(args);
        if (opts.Rest.Count != 1)
        {
            throw new ArgError("usage: islebar rm <id>");
        }

        var store = StoreFor(opts);
        opts.WarnUnknown(error);
        var removed = store.Remove(opts.Rest[0]);
        output.WriteLine(removed ? "Removed." : "No such state.");
        return removed ? ExitOk : ExitFailed;
    }

    // ---------------- timer ----------------

    private static int Timer(string[] args, TextWriter output, TextWriter error)
    {
        var opts = ArgMap.Parse(args);
        var input = string.Join(' ', opts.Rest);
        if (!TimerParser.TryParse(input, out var duration))
        {
            error.WriteLine($"islebar: '{input}' is not a valid duration. Examples: 25m · 1h30m · 90s (Korean units like 25분 also work)");
            return ExitUsage;
        }

        var now = DateTimeOffset.UtcNow;
        var state = TimerParser.ToActivity(duration, now, opts.Value("label"));
        var store = StoreFor(opts);
        var path = store.Write(opts.Value("id") ?? $"timer_{Environment.ProcessId}", state);
        opts.WarnUnknown(error);
        output.WriteLine($"{duration.TotalSeconds:0}s timer → {path}");
        return ExitOk;
    }

    // ---------------- help ----------------

    private static int ShowArgs(string[] args, TextWriter output, TextWriter error)
    {
        var opts = ArgMap.Parse(args);
        var question = string.Join(' ', opts.Rest);
        var options = new IsleBar.Core.Configuration.LaunchOptions
        {
            Session = opts.Value("session") ?? "new",
            Model = opts.Value("model") ?? "default",
            Effort = opts.Value("effort") ?? "default",
            Perm = opts.Value("perm") ?? "bypass",
            Rc = opts.Has("rc"),
        };
        var agentName = opts.Value("agent") ?? AgentKind.Claude;
        if (!AgentKind.IsKnown(agentName))
        {
            error.WriteLine($"Unknown agent: {agentName} (available: {string.Join(", ", AgentKind.All)})");
            return ExitUsage;
        }

        opts.WarnUnknown(error);
        var profile = AgentProfiles.Get(agentName);

        // if an option the agent does not use was given, report it rather than silently dropping it
        foreach (var name in LaunchOptionDefs.All)
        {
            if (!profile.Supports(name) && (opts.Value(name) is not null || opts.Has(name)))
            {
                error.WriteLine($"{profile.Name} has no --{name} option; ignored.");
            }
        }

        var built = profile.BuildArgs(profile.Name, question, options);
        output.WriteLine(ClaudeArguments.ToDisplayString(built));
        return ExitOk;
    }

    /// <summary>
    /// Shows the slash commands usable in this folder right now.
    /// Project commands only appear when the working folder is that project, so use this to check why one is missing.
    /// </summary>
    private static int Commands(string[] args, TextWriter output, TextWriter error)
    {
        var opts = ArgMap.Parse(args);

        // read the values first so they are not wrongly flagged as "unknown option"
        var folder = opts.Value("folder") ?? Directory.GetCurrentDirectory();
        var home = opts.Value("home")
            ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        opts.WarnUnknown(error);

        var all = SlashCommandScanner.Scan(folder, home);
        var filter = opts.Rest.FirstOrDefault();
        var shown = filter is null
            ? all
            : SlashCommandMatcher.Match(all, filter.StartsWith('/') ? filter : "/" + filter, int.MaxValue);

        if (shown.Count == 0)
        {
            output.WriteLine(all.Count == 0
                ? $"No commands available. (folder {folder} · home {home})"
                : "No matching commands.");
            return ExitOk;
        }

        foreach (var c in shown)
        {
            var where = c.Source switch
            {
                CommandSource.Project => "project",
                CommandSource.User => "user",
                _ => "plugin",
            };
            var desc = c.Description.Length > 0 ? "  " + c.Description : string.Empty;
            output.WriteLine($"{c.Display,-32} [{where}]{desc}");
        }

        return ExitOk;
    }

    private static int Aliases(string[] args, TextWriter output, TextWriter error)
    {
        var file = ArgMap.Parse(args).Rest.FirstOrDefault();
        string? markdown;
        if (file is not null)
        {
            markdown = File.ReadAllText(file);
        }
        else
        {
            using var source = new HttpModelAliasSource();
            markdown = source.FetchAsync().GetAwaiter().GetResult();
        }

        var found = ModelAliasParser.Parse(markdown);
        if (found is null)
        {
            error.WriteLine("islebar: alias list looks unreliable (no table, or fewer than 3 entries). Keeping the current list.");
            return ExitFailed;
        }

        output.WriteLine(string.Join(' ', found));
        return ExitOk;
    }

    private static ActivityStore StoreFor(ArgMap opts)
    {
        var dir = opts.Value("dir");
        return dir is null ? ActivityStore.CreateDefault() : new ActivityStore(dir);
    }

    private static void PrintUsage(TextWriter output) => output.Write(
        """
        islebar — push state onto the IsleBar island

        Usage:
          islebar push [options]          write one state (the same --id overwrites it)
          islebar timer <duration>        start a timer. e.g. 25m · 1h30m · 90s (Korean units like 25분 also work)
          islebar list                    the states IsleBar's state files hold now (each kind lingers for its own time)
          islebar show                    which of those the island would show + how many more (music, downloads and
                                          system notices come from Windows, not these files)
          islebar rm <id>                 remove a state
          islebar hook [--agent codex]    post an agent notification to the island (Claude hooks: JSON on stdin, Codex notify: JSON as last argument)
          islebar args [question] [opts]  preview how claude/codex would be invoked
          islebar commands [text]         list slash commands usable in the current folder
          islebar aliases [file]          model alias list (from the file if given, otherwise from the official docs)

        push options:
          --kind   transfer | notice | timer | music | agent-working | agent-permission | agent-done | error  (default transfer)
                   (an agent-… item from push has no terminal to follow: remove it with islebar rm <id>)
          --state  run | done | error  (default run)
          --title  title           e.g. "📥 Phone → PC"
          --name   name            truncated with "…" to fit the slot width
          --stage  stage           "finish" shows as "finishing"
          --total  total bytes
          --done   bytes done      omit for unknown progress (indeterminate bar)
          --msg    one-line message
          --open   path to open when clicked after finishing
          --t0     start time (Unix seconds)   --due  end time (timer)
          --id     state name (default: islebar_<process id> — give one to update the same item instead of adding)
          --dir    state folder (default: %LOCALAPPDATA%\IsleBar\state)

        timer options: --label <text>
        args options: --agent claude|codex  --session new|continue|resume  --model <alias>  --effort ...  --perm bypass|auto|plan|ask  --rc
        (codex has no --effort or --rc; if given, they are ignored with a notice)

        """);

    private sealed class ArgError(string message) : Exception(message);

    /// <summary>--name/value pairs. A --name without a value is stored as "" (flag).</summary>
    private sealed class ArgMap
    {
        private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
        private readonly HashSet<string> _used = new(StringComparer.Ordinal);

        /// <summary>
        /// Splits options from the remaining words in a single pass. An option's <b>value</b> must never
        /// leak into the remaining words — if it did, `islebar timer --dir X 25m` would read "X 25m" as the duration.
        /// </summary>
        public static ArgMap Parse(string[] args)
        {
            var map = new ArgMap();
            for (var i = 0; i < args.Length; i++)
            {
                if (!args[i].StartsWith("--", StringComparison.Ordinal))
                {
                    map.Rest.Add(args[i]);
                    continue;
                }

                var name = args[i][2..];
                var hasValue = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
                map._values[name] = hasValue ? args[++i] : string.Empty;
            }

            return map;
        }

        /// <summary>Words that are neither options nor option values (question, id, duration, etc.).</summary>
        public List<string> Rest { get; } = [];

        /// <summary>Whether the flag was present (true even without a value).</summary>
        public bool Has(string name)
        {
            _used.Add(name);
            return _values.ContainsKey(name);
        }

        public string? Value(string name)
        {
            _used.Add(name);
            return _values.TryGetValue(name, out var value) ? value : null;
        }

        public long? Long(string name)
            => Value(name) is { Length: > 0 } text && long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)
                ? v
                : null;

        public double? Double(string name)
            => Value(name) is { Length: > 0 } text && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var v)
                ? v
                : null;

        /// <summary>Reports options dropped because of typos (silently ignoring them leaves no clue why things fail).</summary>
        public void WarnUnknown(TextWriter error)
        {
            foreach (var name in _values.Keys.Where(k => !_used.Contains(k)))
            {
                error.WriteLine($"islebar: unknown option --{name} (ignored)");
            }
        }
    }
}
