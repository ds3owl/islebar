namespace IsleBar.Core.Supervisor;

/// <summary>
/// Supervisor wait time. A restart request (4 — theme change etc.) relaunches immediately; other deaths wait from 1 second, doubling up to 60 seconds.
/// If it ran fine for a long time (over 1 minute) before dying, counting starts over — same rule as the Python version's claude_bar_run.pyw.
/// </summary>
public sealed class RelaunchBackoff
{
    public static readonly TimeSpan Max = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan HealthyRun = TimeSpan.FromMinutes(1);
    private TimeSpan _next = TimeSpan.FromSeconds(1);

    /// <summary>How long to wait after this exit before relaunching.</summary>
    public TimeSpan After(int exitCode, TimeSpan ranFor)
    {
        if (ranFor >= HealthyRun)
        {
            _next = TimeSpan.FromSeconds(1);   // ran fine for a good while, so start over
        }

        if (exitCode == ExitCodes.RestartRequested)
        {
            return TimeSpan.Zero;
        }

        var wait = _next;
        _next = TimeSpan.FromTicks(Math.Min(Max.Ticks, _next.Ticks * 2));
        return wait;
    }
}
