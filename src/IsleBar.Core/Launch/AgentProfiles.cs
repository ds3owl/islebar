namespace IsleBar.Core.Launch;

/// <summary>Finds an agent by name. Unknown names fall back to Claude.</summary>
public static class AgentProfiles
{
    public static readonly IReadOnlyList<IAgentProfile> All = [ClaudeProfile.Instance, CodexProfile.Instance];

    /// <summary>
    /// The profile matching the name. Falls back to <see cref="ClaudeProfile"/> so launching is not
    /// blocked even if a corrupted settings file supplies an unknown value.
    /// </summary>
    public static IAgentProfile Get(string? name)
    {
        foreach (var p in All)
        {
            if (string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return p;
            }
        }

        return ClaudeProfile.Instance;
    }
}
