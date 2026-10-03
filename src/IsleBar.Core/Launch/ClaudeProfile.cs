using IsleBar.Core.Configuration;

namespace IsleBar.Core.Launch;

/// <summary>
/// Claude Code. Argument building reuses the existing <see cref="ClaudeArguments"/> as-is
/// (it must match the Python version's <c>build_args</c>, so the rules are not rewritten in this class).
/// </summary>
public sealed class ClaudeProfile : IAgentProfile
{
    public static readonly ClaudeProfile Instance = new();

    private ClaudeProfile()
    {
    }

    public string Name => AgentKind.Claude;

    public string WindowsFileName => ClaudeExecutable.WindowsFileName;

    /// <summary>Location when installed without npm.</summary>
    public IReadOnlyList<string> WindowsFileNames { get; } = [ClaudeExecutable.WindowsFileName, "claude.cmd"];

    public IReadOnlyList<string> FallbackRelativePaths { get; } = [Path.Combine(".local", "bin"), Path.Combine("AppData", "Roaming", "npm")];

    public IReadOnlyList<string> DefaultModels { get; } = [.. LaunchOptionDefs.DefaultModels];

    /// <summary>Updates aliases by parsing the model docs (every 6 hours).</summary>
    public bool SupportsModelAutoUpdate => true;

    /// <summary>Claude uses all five options.</summary>
    public bool Supports(string option) => LaunchOptionDefs.All.Contains(option);

    public IReadOnlyList<string> BuildArgs(string executablePath, string? question, LaunchOptions options)
        => ClaudeArguments.Build(executablePath, question, options);
}
