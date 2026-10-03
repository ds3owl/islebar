namespace IsleBar.Core.Ui;

/// <summary>
/// Spring curve defined by response time and damping ratio (dampingRatio = 1 − bounce).
/// In WinUI's spring animation the meaning of Period does not match these values (and the docs are vague), so getting this feel was hard —
/// so the curve is computed here directly and baked into keyframes (researched 09-30). Kept in Core so it can be tested without a window.
/// </summary>
public static class SpringCurve
{
    /// <summary>
    /// Progress at time <paramref name="seconds"/> (starts at 0 and goes to 1; exceeds 1 on overshoot).
    /// No overshoot when the damping ratio is 1 or more (critical damping).
    /// </summary>
    public static double Progress(double seconds, double response, double dampingRatio)
    {
        if (seconds <= 0)
        {
            return 0;
        }

        var omega = 2 * Math.PI / response;
        if (dampingRatio >= 0.999)
        {
            return 1 - (Math.Exp(-omega * seconds) * (1 + (omega * seconds)));
        }

        var damped = omega * Math.Sqrt(1 - (dampingRatio * dampingRatio));
        var decay = Math.Exp(-dampingRatio * omega * seconds);
        return 1 - (decay * (Math.Cos(damped * seconds) + (dampingRatio * omega / damped * Math.Sin(damped * seconds))));
    }

    /// <summary>After this time, the difference from 1 is below <paramref name="tolerance"/> (motion is invisible).</summary>
    public static double SettleTime(double response, double dampingRatio, double tolerance = 0.002)
    {
        const double step = 0.001, limit = 5.0;
        for (var t = limit; t > 0; t -= step)
        {
            if (Math.Abs(1 - Progress(t, response, dampingRatio)) >= tolerance)
            {
                return Math.Min(limit, t + step);
            }
        }

        return step;
    }

    /// <summary>Points to bake into keyframes: (time fraction 0–1, progress). The first point is (0,0), the last (1,1).</summary>
    public static IReadOnlyList<(float Fraction, float Progress)> Sample(double response, double dampingRatio, int count = 40)
    {
        var total = SettleTime(response, dampingRatio);
        var points = new List<(float, float)>(count + 1);
        for (var i = 0; i <= count; i++)
        {
            var f = (double)i / count;
            points.Add(((float)f, i == count ? 1f : (float)Progress(f * total, response, dampingRatio)));
        }

        return points;
    }
}
