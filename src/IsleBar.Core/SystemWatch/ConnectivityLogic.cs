namespace IsleBar.Core.SystemWatch;

/// <summary>Outcome of an internet connectivity notice.</summary>
public enum ConnectivityChange
{
    None,

    /// <summary>Lost → "No internet" until it recovers.</summary>
    Lost,

    /// <summary>Came back after a loss was reported → "Back online" briefly.</summary>
    Restored,
}

/// <summary>
/// Rules for internet connectivity notices. When switching Wi-Fi or waking from sleep, Windows flips between "none → local only → internet"
/// within 1–2 seconds — reporting each would make the island flicker. So a value is confirmed only when <b>it holds for 3 seconds</b>.
/// The first confirmed value at startup is just the baseline and is not reported. "Back online" is only emitted if a loss was reported
/// (being offline at startup and then connecting is a loss nobody saw, so it passes silently).
/// </summary>
public sealed class ConnectivityLogic
{
    /// <summary>A value must stay unchanged this long to be confirmed.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private bool? _pending;
    private DateTimeOffset _pendingSince;
    private bool? _stable;

    /// <summary>Whether the offline notice is up.</summary>
    public bool LostShown { get; private set; }

    /// <summary>Feeds the connection state just read. Confirmation is done by <see cref="Tick"/>.</summary>
    public void Report(bool online, DateTimeOffset now)
    {
        if (_pending != online)
        {
            _pending = online;
            _pendingSince = now;
        }
    }

    /// <summary>Signals that time has passed and returns any confirmed change. Call roughly every second.</summary>
    public ConnectivityChange Tick(DateTimeOffset now)
    {
        if (_pending is not { } value || value == _stable || now - _pendingSince < Debounce)
        {
            return ConnectivityChange.None;
        }

        var first = _stable is null;
        _stable = value;
        if (first)
        {
            return ConnectivityChange.None;   // value at startup = baseline
        }

        if (!value)
        {
            LostShown = true;
            return ConnectivityChange.Lost;
        }

        if (LostShown)
        {
            LostShown = false;
            return ConnectivityChange.Restored;
        }

        return ConnectivityChange.None;
    }
}
