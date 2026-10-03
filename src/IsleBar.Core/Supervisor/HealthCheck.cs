namespace IsleBar.Core.Supervisor;

/// <summary>
/// Exit codes exchanged with the supervisor. Same meanings as the Python version.
/// </summary>
public static class ExitCodes
{
    /// <summary>The user quit — <b>do not relaunch</b>.</summary>
    public const int UserQuit = 0;

    /// <summary>Exited on its own because something was wrong → the supervisor relaunches it.</summary>
    public const int Abnormal = 3;

    /// <summary>Please restart (model buttons were added or the theme changed).</summary>
    public const int RestartRequested = 4;

    /// <summary>Already running → the supervisor does nothing.</summary>
    public const int AlreadyRunning = 5;

    /// <summary>Whether this code means a relaunch is needed.</summary>
    /// <summary>
    /// Whether this exit should be relaunched: not if the user quit (0) or it is already running (5); <b>everything else</b> is relaunched —
    /// not just abnormal (3) and restart-requested (4), but unexpected deaths (e.g. 0xC000027B) too, so the search bar never stays gone.
    /// </summary>
    public static bool ShouldRelaunch(int code) => code is not (UserQuit or AlreadyRunning);
}

/// <summary>Why a restart is happening.</summary>
public enum HealthProblem
{
    /// <summary>All fine.</summary>
    None,

    /// <summary>The window disappeared.</summary>
    WindowGone,

    /// <summary>Explorer restarted, so the taskbar we were attached to changed.</summary>
    ExplorerRestarted,

    /// <summary>The light/dark theme changed.</summary>
    ThemeChanged,

    /// <summary>The taskbar's display scaling changed (scale setting, or docked onto a monitor with another scale).</summary>
    DpiChanged,
}

/// <summary>Check result.</summary>
/// <param name="Problem">What the problem is.</param>
/// <param name="ExitCode">Which code to exit with as a result.</param>
/// <param name="Reason">Korean text to write to the log.</param>
/// <param name="SettleDelay">How long to wait before exiting.</param>
public readonly record struct HealthVerdict(
    HealthProblem Problem, int ExitCode, string Reason, TimeSpan SettleDelay)
{
    public bool Healthy => Problem == HealthProblem.None;
}

/// <summary>
/// Rules for whether the resident process should exit on its own.
///
/// <para><b>Why this lives in Core.</b> If this judgement is wrong, the bar dies silently or relaunches forever.
/// Inside window code it could only be verified by actually killing things on a PC, but since all inputs are booleans
/// everything can be verified here.</para>
///
/// <para><b>Theme changes wait 2 seconds.</b> Relaunching before all colors have switched bakes in the wrong colors
/// (HANDOFF lesson).</para>
/// </summary>
public static class HealthCheck
{
    /// <summary>How long to wait for the theme to finish switching.</summary>
    public static readonly TimeSpan ThemeSettleDelay = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Decides what to do based on the current state. The order of checks matters —
    /// if the window is gone, nothing else is worth checking.
    /// </summary>
    /// <param name="windowAlive">Whether our window still exists.</param>
    /// <param name="attachedTaskbar">Handle of the taskbar we attached to (0 = not attached yet).</param>
    /// <param name="currentTaskbar">Handle of the taskbar found now.</param>
    /// <param name="startedLight">Whether the theme was light at startup.</param>
    /// <param name="nowLight">Whether the theme is light now.</param>
    /// <param name="startedDpi">The taskbar's DPI when we attached (0 = unknown).</param>
    /// <param name="nowDpi">The taskbar's DPI now (0 = unknown).</param>
    public static HealthVerdict Check(
        bool windowAlive,
        nint attachedTaskbar,
        nint currentTaskbar,
        bool startedLight,
        bool nowLight,
        uint startedDpi = 0,
        uint nowDpi = 0)
    {
        if (!windowAlive)
        {
            return new HealthVerdict(HealthProblem.WindowGone, ExitCodes.Abnormal, "window gone", TimeSpan.Zero);
        }

        // not attached yet (0) means nothing to compare — only check after attaching
        if (attachedTaskbar != 0 && currentTaskbar != attachedTaskbar)
        {
            return new HealthVerdict(
                HealthProblem.ExplorerRestarted, ExitCodes.Abnormal, "explorer restarted", TimeSpan.Zero);
        }

        if (nowLight != startedLight)
        {
            return new HealthVerdict(
                HealthProblem.ThemeChanged, ExitCodes.RestartRequested, "theme changed", ThemeSettleDelay);
        }

        // The pill is laid out once for the DPI it started with; after a scale change it kept the old size and covered the
        // Start button (found 10-01). Relaunching lays it out again — wait like a theme change so the taskbar has settled first.
        if (startedDpi != 0 && nowDpi != 0 && startedDpi != nowDpi)
        {
            return new HealthVerdict(
                HealthProblem.DpiChanged, ExitCodes.RestartRequested, $"display scale changed ({startedDpi} → {nowDpi} dpi)", ThemeSettleDelay);
        }

        return new HealthVerdict(HealthProblem.None, ExitCodes.UserQuit, string.Empty, TimeSpan.Zero);
    }
}
