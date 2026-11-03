namespace IsleBar.Core.Supervisor;

/// <summary>
/// Supervisor wait time. A restart request (4 — theme change etc.) relaunches immediately; other deaths wait from 1 second, doubling up to 60 seconds.
/// If it ran fine for a long time (over 1 minute) before dying, counting starts over — same rule as the Python version's claude_bar_run.pyw.
/// After <see cref="CrashLoop"/> quick deaths in a row it waits <see cref="CrashLoopWait"/>: a bar that dies at every start used to
/// be relaunched every minute forever, one crash report each time (review 11-03).
/// </summary>
public sealed class RelaunchBackoff
{
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(60);
    public const int CrashLoop = 10;
    public static readonly TimeSpan CrashLoopWait = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(1);
    private TimeSpan _next = TimeSpan.FromSeconds(1);
    private int _quickDeaths;

    /// <summary>Whether the last <see cref="After"/> came from a crash loop (for the supervisor's log).</summary>
    public bool InCrashLoop => _quickDeaths >= CrashLoop;

    /// <summary>How long to wait after this exit before relaunching.</summary>
    public TimeSpan After(int exitCode, TimeSpan ranFor)
    {
        if (ranFor >= HealthyRun)
        {
            _next = TimeSpan.FromSeconds(1);   // ran fine for a good while, so start over
            _quickDeaths = 0;
        }

        if (exitCode == ExitCodes.RestartRequested)
        {
            return TimeSpan.Zero;
        }

        if (++_quickDeaths >= CrashLoop)
        {
            return CrashLoopWait;
        }

        var wait = _next;
        _next = TimeSpan.FromTicks(Math.Min(Max.Ticks, _next.Ticks * 2));
        return wait;
    }
}
