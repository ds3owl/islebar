namespace IsleBar.Core.SystemWatch;

/// <summary>A Bluetooth device connection change.</summary>
/// <param name="Name">Device name (the name the user gave it).</param>
/// <param name="Connected">true = connected, false = disconnected.</param>
public readonly record struct ConnectionChange(string Id, string Name, bool Connected);

/// <summary>
/// Remembers Bluetooth devices' connection state and picks out only "the moment it changed".
/// <list type="bullet">
/// <item>Windows' device watcher reports all already-connected devices right at startup — that is not a change, so
/// until <see cref="EnumerationCompleted"/> they are only remembered, not reported.</item>
/// <item>Wireless earbuds show up twice for the same device, as "Classic" and "Low Energy (LE)" — if the same name changes
/// the same way within a short time, it is reported once.</item>
/// <item>Events that do not say whether it is connected (e.g. only the name changed) keep the existing value.</item>
/// </list>
/// </summary>
public sealed class ConnectionTracker
{
    /// <summary>Window within which the same change for the same name counts as one.</summary>
    public static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(5);

    private readonly Dictionary<string, (string Name, bool Connected)> _devices = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Name, bool Connected), DateTimeOffset> _recent = [];

    /// <summary>Whether the initial list has been fully received. Changes before that are not reported.</summary>
    public bool EnumerationCompleted { get; private set; }

    /// <summary>The initial list has been fully received (Windows' EnumerationCompleted).</summary>
    public void CompleteEnumeration() => EnumerationCompleted = true;

    /// <summary>
    /// A device appeared or changed. If <paramref name="connected"/> is null, the connection state is left as-is.
    /// Returns the change if there is one to report.
    /// </summary>
    public ConnectionChange? Update(string id, string? name, bool? connected, DateTimeOffset now)
    {
        _devices.TryGetValue(id, out var known);
        var hadEntry = _devices.ContainsKey(id);
        var newName = string.IsNullOrWhiteSpace(name) ? known.Name : name.Trim();
        var newConnected = connected ?? (hadEntry && known.Connected);
        _devices[id] = (newName ?? string.Empty, newConnected);

        var changed = hadEntry ? known.Connected != newConnected : newConnected;
        return changed ? Announce(id, newName, newConnected, now) : null;
    }

    /// <summary>A device dropped from the list (unpaired etc.). Reported as disconnected if it was connected.</summary>
    public ConnectionChange? Remove(string id, DateTimeOffset now)
    {
        if (!_devices.Remove(id, out var known) || !known.Connected)
        {
            return null;
        }

        return Announce(id, known.Name, false, now);
    }

    /// <summary>Number of devices currently known to be connected.</summary>
    public int ConnectedCount => _devices.Values.Count(d => d.Connected);

    private ConnectionChange? Announce(string id, string? name, bool connected, DateTimeOffset now)
    {
        if (!EnumerationCompleted || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        foreach (var stale in _recent.Where(r => now - r.Value > DuplicateWindow).Select(r => r.Key).ToList())
        {
            _recent.Remove(stale);
        }

        var key = (name, connected);
        if (_recent.ContainsKey(key))
        {
            return null;   // Classic/LE twin
        }

        _recent[key] = now;
        return new ConnectionChange(id, name, connected);
    }
}
