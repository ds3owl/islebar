namespace IsleBar.Core.SystemWatch;

/// <summary>
/// "On when high for a while, off only after low for a while." Brief CPU spikes to 100% (launching a program, starting a build) are common, so
/// reporting every one is noise — it turns on <b>only when 90% or more lasts 30 seconds</b>, and once on it must stay below 75%
/// for 10 seconds to turn off (two separate thresholds so it does not flicker on and off near the threshold).
/// </summary>
public sealed class SustainedThreshold
{
    private readonly double _onAt;
    private readonly TimeSpan _onFor;
    private readonly double _offBelow;
    private readonly TimeSpan _offFor;
    private DateTimeOffset? _highSince;
    private DateTimeOffset? _lowSince;

    public SustainedThreshold(double onAt, TimeSpan onFor, double offBelow, TimeSpan offFor)
    {
        _onAt = onAt;
        _onFor = onFor;
        _offBelow = offBelow;
        _offFor = offFor;
    }

    /// <summary>CPU rule: 90% for 30 s → on, below 75% for 10 s → off.</summary>
    public static SustainedThreshold Cpu() => new(90, TimeSpan.FromSeconds(30), 75, TimeSpan.FromSeconds(10));

    /// <summary>Memory rule: 92% for 30 s → on, below 75% for 10 s → off.</summary>
    public static SustainedThreshold Memory() => new(92, TimeSpan.FromSeconds(30), 75, TimeSpan.FromSeconds(10));

    /// <summary>Whether a warning is active now.</summary>
    public bool Active { get; private set; }

    /// <summary>
    /// Whether it is past the on-threshold and waiting, or already on — only then is the expensive work (finding the busiest program) done.
    /// </summary>
    public bool Elevated => Active || _highSince is not null;

    /// <summary>Feeds a new value and returns whether to warn.</summary>
    public bool Update(double value, DateTimeOffset now)
    {
        if (!Active)
        {
            if (value >= _onAt)
            {
                _highSince ??= now;
                if (now - _highSince.Value >= _onFor)
                {
                    Active = true;
                    _highSince = null;
                    _lowSince = null;
                }
            }
            else
            {
                _highSince = null;
            }
        }
        else if (value < _offBelow)
        {
            _lowSince ??= now;
            if (now - _lowSince.Value >= _offFor)
            {
                Active = false;
                _lowSince = null;
            }
        }
        else
        {
            _lowSince = null;
        }

        return Active;
    }
}

/// <summary>CPU usage calculation (difference between two GetSystemTimes reads).</summary>
public static class CpuMath
{
    /// <summary>
    /// Overall CPU usage %. GetSystemTimes' kernel time <b>includes</b> idle time, so
    /// busy = (kernel + user − idle), total = (kernel + user). 0 if the difference is 0 (read again too quickly).
    /// </summary>
    public static double TotalPercent(long idleDelta, long kernelDelta, long userDelta)
    {
        var total = kernelDelta + userDelta;
        if (total <= 0)
        {
            return 0;
        }

        return Math.Clamp((total - idleDelta) * 100.0 / total, 0, 100);
    }

    /// <summary>
    /// Usage % of one process (relative to all cores combined, 0–100). <paramref name="cpuDelta"/> = that process's CPU time increase,
    /// <paramref name="wallDelta"/> = actual elapsed time.
    /// </summary>
    public static double ProcessPercent(TimeSpan cpuDelta, TimeSpan wallDelta, int processorCount)
    {
        if (wallDelta <= TimeSpan.Zero || processorCount <= 0)
        {
            return 0;
        }

        return Math.Clamp(cpuDelta.TotalMilliseconds * 100.0 / (wallDelta.TotalMilliseconds * processorCount), 0, 100);
    }
}
