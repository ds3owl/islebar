using IsleBar.Core.Configuration;

namespace IsleBar.Core.Launch;

/// <summary>
/// Builds the <c>claude</c> launch arguments. Order and values must match the Python version's <c>build_args</c>.
///
/// The question comes <b>first</b>: <c>--remote-control [name]</c> and <c>--resume [value]</c>
/// take values, so a question placed after them would be swallowed as the session name.
/// </summary>
public static class ClaudeArguments
{
    /// <summary>Argument added when everything is allowed (bypass). The user always uses this.</summary>
    public const string BypassFlag = "--dangerously-skip-permissions";

    /// <summary>Opus is sent as the long-context variant (1M tokens) normally used.</summary>
    public const string OpusAlias = "opus";
    public const string OpusLongContext = "opus[1m]";

    /// <summary>
    /// Builds the argument list. The first element is <paramref name="claudePath"/> (the executable) as-is.
    /// </summary>
    /// <param name="claudePath">Path to the claude executable.</param>
    /// <param name="question">The question. If empty, launches without a question.</param>
    /// <param name="options">Launch options.</param>
    private static readonly HashSet<string> Subcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "config", "mcp", "migrate-installer", "setup-token", "doctor", "update", "upgrade", "install", "plugin", "plugins", "agents",
    };

    /// <summary>Whether the whole question is one of Claude Code's subcommand names.</summary>
    public static bool IsSubcommand(string question) => Subcommands.Contains(question.Trim());

    public static IReadOnlyList<string> Build(string claudePath, string? question, LaunchOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(claudePath);
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string> { claudePath };

        var q = (question ?? string.Empty).Trim();
        if (q.Length > 0)
        {
            // prepend a space so a question like '-v why…' is not read as an option, nor a one-word question like "update" or
            // "doctor" as Claude's own subcommand (it ran instead of being asked — review 10-03)
            args.Add(q.StartsWith('-') || IsSubcommand(q) ? " " + q : q);
        }

        switch (options.Perm)
        {
            case "bypass": args.Add(BypassFlag); break;
            case "auto": args.Add("--permission-mode"); args.Add("auto"); break;
            case "plan": args.Add("--permission-mode"); args.Add("plan"); break;
            // "ask" = claude's default behaviour → add nothing
        }

        switch (options.Session)
        {
            case "continue": args.Add("--continue"); break;
            case "resume": args.Add("--resume"); break;
            // "new" = add nothing
        }

        if (options.Model != "default" && options.Model.Length > 0)
        {
            args.Add("--model");
            args.Add(options.Model == OpusAlias ? OpusLongContext : options.Model);
        }

        if (options.Effort != "default" && options.Effort.Length > 0)
        {
            args.Add("--effort");
            args.Add(options.Effort);
        }

        if (options.Rc)
        {
            args.Add("--remote-control");
        }

        return args;
    }

    /// <summary>
    /// A single line for display. Do not use it for launching — launching passes the <see cref="Build"/> list as-is
    /// (leaving quoting to the shell breaks on quote characters inside the question).
    /// </summary>
    public static string ToDisplayString(IReadOnlyList<string> args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return string.Join(' ', args.Select(a => a.Contains(' ', StringComparison.Ordinal) ? '"' + a + '"' : a));
    }
}
