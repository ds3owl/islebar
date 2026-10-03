namespace IsleBar.Core.Island;

/// <summary>
/// The kind of work shown on the island. <b>Value order is priority</b> — smaller values are more urgent.
/// (permission prompt &gt; error &gt; transfer &gt; short notice &gt; timer &gt; music &gt; task done &gt; task working.
/// 10-01: music sits above the agent task states so a background task doesn't hide the song you're playing.)
/// The agent name lives separately in <see cref="ActivityState.Agent"/> — Claude and Codex share the same slot.
/// </summary>
public enum ActivityKind
{
    /// <summary>An agent (Claude/Codex) is asking for permission — a human must answer to proceed. Most urgent.</summary>
    AgentPermission = 0,

    /// <summary>Something failed.</summary>
    Error = 1,

    /// <summary>A file transfer/download is in progress.</summary>
    Transfer = 2,

    /// <summary>
    /// Short notice (charging, Bluetooth, internet, copy, calendar, etc.; feature added 09-30). It only stays up a few seconds, so
    /// it comes before the long-lived "task done" — it shows briefly, then the island returns to what it was.
    /// The icon goes in <see cref="ActivityState.Glyph"/>; if a human needs to act (low battery, internet lost), State=error gives an orange border.
    /// </summary>
    Notice = 3,

    /// <summary>A timer/Pomodoro is running.</summary>
    Timer = 4,

    /// <summary>
    /// Music/video is playing. Sits above the agent task states (10-01): a task running or finishing in the background should not
    /// hide the song you chose to play. A finished task still chimes and shows as the ● more-dot, and takes the pill once music stops.
    /// </summary>
    Music = 5,

    /// <summary>An agent task has finished.</summary>
    AgentDone = 6,

    /// <summary>
    /// An agent (Claude/Codex) is working on a prompt — shown while nothing more urgent is up, replaced in place by "done" when it
    /// finishes (same file). Needs the UserPromptSubmit hook. Least urgent.
    /// </summary>
    AgentWorking = 7,
}

/// <summary>Transfer direction. A title ending in "PC" counts as incoming (same rule as the Python version).</summary>
public enum TransferDirection
{
    /// <summary>Phone → PC (⬇)</summary>
    Incoming,

    /// <summary>PC → phone (⬆)</summary>
    Outgoing,
}

/// <summary>Run state.</summary>
public enum ActivityRunState
{
    Running,
    Done,
    Error,
}
