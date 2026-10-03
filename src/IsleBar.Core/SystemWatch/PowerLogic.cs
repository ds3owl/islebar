namespace IsleBar.Core.SystemWatch;

/// <summary>A single power status reading.</summary>
/// <param name="HasBattery">Whether there is a battery. False on desktops — then no notices are emitted.</param>
/// <param name="PluggedIn">Whether the charger is plugged in.</param>
/// <param name="Percent">Remaining charge 0–100.</param>
public readonly record struct PowerReading(bool HasBattery, bool PluggedIn, int Percent);

/// <summary>What a power change produces.</summary>
public enum PowerChange
{
    /// <summary>Nothing changed (or first reading — nothing is reported at startup).</summary>
    None,

    /// <summary>Charger plugged in → "Charging · 78%" briefly.</summary>
    PluggedIn,

    /// <summary>Charger unplugged → "On battery · 76%" briefly.</summary>
    Unplugged,
}

/// <summary>
/// Rules for power notices. Windows events arrive several times over at the moment of plugging in (status, percent and time remaining change separately),
/// and the state at app startup is not a "change", so we compare with the previous state and report <b>only when plugged/unplugged actually changed</b>.
/// Low battery is not a change but a <b>state</b>: it stays up while at 20% or less without a charger, and goes away
/// when plugged in or above 20% (shown even if already low at startup — since a human needs to act).
/// </summary>
public sealed class PowerLogic
{
    /// <summary>Low-battery thresholds. Once at 20%, once more at 10% (the text changes so it catches the eye again).</summary>
    public static readonly int[] LowThresholds = [20, 10];

    private PowerReading? _last;

    /// <summary>Feeds a newly read state and returns the change to report.</summary>
    public PowerChange Observe(PowerReading reading)
    {
        var last = _last;
        _last = reading;
        if (!reading.HasBattery || last is not { HasBattery: true } previous)
        {
            return PowerChange.None;
        }

        if (previous.PluggedIn == reading.PluggedIn)
        {
            return PowerChange.None;
        }

        return reading.PluggedIn ? PowerChange.PluggedIn : PowerChange.Unplugged;
    }

    /// <summary>
    /// The low-battery threshold currently in effect (20 or 10). Null when charging or with plenty left — the warning is taken down.
    /// When the value goes from 20 → 10, the watcher posts the warning anew.
    /// </summary>
    public static int? LowLevel(PowerReading reading)
    {
        if (!reading.HasBattery || reading.PluggedIn)
        {
            return null;
        }

        int? level = null;
        foreach (var threshold in LowThresholds)
        {
            if (reading.Percent <= threshold)
            {
                level = threshold;
            }
        }

        return level;
    }
}
