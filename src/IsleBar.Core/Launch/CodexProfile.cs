using IsleBar.Core.Configuration;

namespace IsleBar.Core.Launch;

/// <summary>
/// Codex CLI.
///
/// <para><b>⚠ Needs verification on PC (stage 0 item).</b> Codex CLI is not installed on this server, so
/// the flag strings below have not actually been checked. Look at <c>codex --help</c> once on the PC and,
/// if they differ, fix <b>only the Flags region of this file</b> — the strings are gathered as constants
/// so they can be changed while leaving the build order and option mapping rules alone.</para>
///
/// <para>Three differences from Claude.
/// ① The question goes <b>last</b> (Claude puts it first — because of value-taking options). Codex takes
///    the form <c>codex "question"</c>, so by convention it goes last.
/// ② Resume is a <b>subcommand</b>, so it must come right after the executable.
/// ③ <b>Effort and remote control (rc) have no equivalent.</b> Effort is baked into the model name
///    itself (sol/terra/luna/astra), and Codex has no remote-control feature.</para>
/// </summary>
public sealed class CodexProfile : IAgentProfile
{
    public static readonly CodexProfile Instance = new();

    private CodexProfile()
    {
    }

    #region Flags — verify with `codex --help` on PC

    /// <summary>Windows executable name.</summary>
    public const string WindowsExecutable = "codex.cmd";

    /// <summary>Resume subcommand. Comes right after the executable.</summary>
    public const string ResumeCommand = "resume";

    /// <summary>Appended after <see cref="ResumeCommand"/> to continue the most recent conversation.</summary>
    public const string ResumeLastFlag = "--last";

    /// <summary>Model selection.</summary>
    public const string ModelFlag = "-m";

    /// <summary>Skips all approvals and sandboxing (equivalent of Claude's bypass).</summary>
    public const string BypassFlag = "--dangerously-bypass-approvals-and-sandbox";

    /// <summary>
    /// Hands approval requests to automatic review (workspace-write sandbox) — equivalent of Claude's auto.
    /// The old <c>--full-auto</c> does not exist in codex-cli 0.153.4 and makes launching fail (checked with <c>codex --help</c> on PC 09-29).
    /// </summary>
    public const string FullAutoFlag = "--approve-for-me";

    #endregion

    /// <summary>
    /// Default model list. Short names (<c>sol</c>, <c>astra</c>) cause errors, so full IDs are used.
    /// Editable in settings.
    /// </summary>
    public static readonly IReadOnlyList<string> Models =
        ["gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-6-astra", "gpt-5.5"];

    public string Name => AgentKind.Codex;

    public string WindowsFileName => WindowsExecutable;

    /// <summary>npm global install location.</summary>
    public IReadOnlyList<string> WindowsFileNames { get; } = [WindowsExecutable, "codex.exe"];

    public IReadOnlyList<string> FallbackRelativePaths { get; } =
        [Path.Combine("AppData", "Roaming", "npm"), Path.Combine(".local", "bin")];

    public IReadOnlyList<string> DefaultModels => Models;

    /// <summary>There is no doc to fetch the model list from — a fixed list is used and edited in settings.</summary>
    public bool SupportsModelAutoUpdate => false;

    /// <summary>Effort and remote control have no equivalent.</summary>
    public bool Supports(string option) => option switch
    {
        LaunchOptionDefs.Effort => false,
        LaunchOptionDefs.Rc => false,
        _ => LaunchOptionDefs.All.Contains(option),
    };

    private static readonly HashSet<string> Subcommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "exec", "e", "review", "login", "logout", "mcp", "mcp-server", "app-server", "completion", "sandbox", "debug", "apply", "a",
        "resume", "fork", "cloud", "features", "help", "generate-ts", "generate-json-schema",
    };

    public IReadOnlyList<string> BuildArgs(string executablePath, string? question, LaunchOptions options)
    {
        ArgumentException.ThrowIfNullOrEmpty(executablePath);
        ArgumentNullException.ThrowIfNull(options);

        var args = new List<string> { executablePath };

        // ① subcommand first (right after the executable)
        switch (options.Session)
        {
            case "continue":
                args.Add(ResumeCommand);
                args.Add(ResumeLastFlag);
                break;
            case "resume":
                args.Add(ResumeCommand);
                break;
            // "new" = no subcommand
        }

        // ② permissions. plan has no Codex equivalent, so nothing is added (default behaviour = ask)
        switch (options.Perm)
        {
            case "bypass": args.Add(BypassFlag); break;
            case "auto": args.Add(FullAutoFlag); break;
            // "plan"/"ask" = add nothing
        }

        // ③ model
        if (options.Model != "default" && options.Model.Length > 0)
        {
            args.Add(ModelFlag);
            args.Add(options.Model);
        }

        // ④ effort/remote control: Supports is false — ignored even if values are set

        // ⑤ question goes last — except for "resume" (the session picker): there the first word after it is the session id, so
        // the question was taken for one and never asked (codex resume [SESSION_ID] [PROMPT] — review 10-03). The picker opens alone.
        var q = (question ?? string.Empty).Trim();
        if (q.Length > 0 && options.Session != "resume")
        {
            // prepend a space so a question like '-v why…' is not read as an option, nor a one-word question like "logout" as
            // Codex's own subcommand (same approach as the Claude version)
            args.Add(q.StartsWith('-') || Subcommands.Contains(q) ? " " + q : q);
        }

        return args;
    }
}
