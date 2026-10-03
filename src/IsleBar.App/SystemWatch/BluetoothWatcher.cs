using IsleBar.Core.SystemWatch;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Bluetooth device connect/disconnect. Watches paired devices (classic + low energy) with <see cref="DeviceWatcher"/>, and
/// the moment <c>System.Devices.Aep.IsConnected</c> changes shows "AirPods · connected" for 3 s / "AirPods · disconnected" for 2 s.
/// <para>
/// Why not the "connected devices only" selector (GetDeviceSelectorFromConnectionStatus): with it, whether Removed arrives
/// on disconnect varies by Windows version, so disconnects can be missed. Watching all paired devices and checking the connection
/// property change directly catches both directions reliably. Rules (ignore the startup list, classic/LE twins only once) are in <see cref="ConnectionTracker"/>.
/// </para>
/// <para>
/// Battery %: after showing the connect notice, looks once for <c>DEVPKEY_Bluetooth_Battery</c> on devices in the same device group (ContainerId),
/// and if found appends "battery 80%" to the hint line. If the device/driver doesn't report it, it's quietly omitted (needs checking on PC).
/// </para>
/// Events come from the thread pool — no UI thread needed.
/// </summary>
internal sealed class BluetoothWatcher : NoticeWatcher
{
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string ContainerIdKey = "System.Devices.Aep.ContainerId";
    private const string CodMajorKey = "System.Devices.Aep.Bluetooth.Cod.Major";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2";
    private const string SettingsUri = "ms-settings:bluetooth";

    /// <summary>Major Bluetooth Class of Device 4 = audio/video (earbuds, headphones, speakers).</summary>
    private const uint MajorAudioVideo = 4;

    private static readonly TimeSpan ConnectedShow = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan DisconnectedShow = TimeSpan.FromSeconds(2);

    /// <summary>Right after connecting the battery value often isn't there yet, so wait a bit before reading.</summary>
    private static readonly TimeSpan BatteryDelay = TimeSpan.FromSeconds(1.5);

    private readonly object _gate = new();
    private readonly ConnectionTracker _tracker = new();
    private readonly Dictionary<string, (Guid? Container, bool Audio)> _info = new(StringComparer.Ordinal);
    private readonly List<DeviceWatcher> _watchers = [];
    private int _enumerating;

    public BluetoothWatcher(Func<Core.Localization.LanguageStrings> strings)
        : base(strings)
    {
    }

    public override void Start() => Guard(() =>
    {
        lock (_gate)
        {
            if (_watchers.Count > 0)
            {
                return;
            }

            foreach (var selector in new[]
                     {
                         BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                         BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                     })
            {
                if (Create(selector) is { } watcher)
                {
                    _watchers.Add(watcher);
                }
            }

            _enumerating = _watchers.Count;
            foreach (var watcher in _watchers)
            {
                watcher.Start();
            }
        }

        // Even if one watcher stops (Aborted) and never signals the end of the initial list, enable notices after 10 s
        _ = Task.Delay(EnumerationTimeout).ContinueWith(_ => Guard(() =>
        {
            lock (_gate)
            {
                _tracker.CompleteEnumeration();
            }
        }), TaskScheduler.Default);
    });

    private static readonly TimeSpan EnumerationTimeout = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Creates one watcher. If Windows rejects the additional property list (supported properties vary by version), recreates it with only the connection property.
    /// </summary>
    private DeviceWatcher? Create(string selector)
    {
        foreach (var properties in new[]
                 {
                     new[] { IsConnectedKey, ContainerIdKey, CodMajorKey },
                     new[] { IsConnectedKey },
                 })
        {
            try
            {
                var watcher = DeviceInformation.CreateWatcher(selector, properties, DeviceInformationKind.AssociationEndpoint);
                watcher.Added += OnAdded;
                watcher.Updated += OnUpdated;
                watcher.Removed += OnRemoved;
                watcher.EnumerationCompleted += OnEnumerationCompleted;
                return watcher;
            }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
            {
                // On to the next (fewer properties)
            }
        }

        return null;
    }

    protected override void Stop()
    {
        lock (_gate)
        {
            foreach (var watcher in _watchers)
            {
                watcher.Added -= OnAdded;
                watcher.Updated -= OnUpdated;
                watcher.Removed -= OnRemoved;
                watcher.EnumerationCompleted -= OnEnumerationCompleted;
                if (watcher.Status is DeviceWatcherStatus.Started or DeviceWatcherStatus.EnumerationCompleted)
                {
                    watcher.Stop();
                }
            }

            _watchers.Clear();
        }
    }

    private void OnAdded(DeviceWatcher sender, DeviceInformation device) => Guard(() =>
    {
        var connected = ReadBool(device.Properties, IsConnectedKey);
        ConnectionChange? change;
        lock (_gate)
        {
            _info[device.Id] = (ReadGuid(device.Properties, ContainerIdKey), ReadUInt(device.Properties, CodMajorKey) == MajorAudioVideo);
            change = _tracker.Update(device.Id, device.Name, connected, DateTimeOffset.UtcNow);
        }

        Announce(change);
    });

    private void OnUpdated(DeviceWatcher sender, DeviceInformationUpdate update) => Guard(() =>
    {
        var connected = ReadBool(update.Properties, IsConnectedKey);
        if (connected is null)
        {
            return;   // property change unrelated to connection (signal strength, etc.)
        }

        ConnectionChange? change;
        lock (_gate)
        {
            change = _tracker.Update(update.Id, null, connected, DateTimeOffset.UtcNow);
        }

        Announce(change);
    });

    private void OnRemoved(DeviceWatcher sender, DeviceInformationUpdate update) => Guard(() =>
    {
        ConnectionChange? change;
        lock (_gate)
        {
            change = _tracker.Remove(update.Id, DateTimeOffset.UtcNow);
            _info.Remove(update.Id);
        }

        Announce(change);
    });

    private void OnEnumerationCompleted(DeviceWatcher sender, object args) => Guard(() =>
    {
        lock (_gate)
        {
            // The "startup list" ends only when both the classic and LE watchers have finished their initial lists
            if (--_enumerating <= 0)
            {
                _tracker.CompleteEnumeration();
            }
        }
    });

    private void Announce(ConnectionChange? change)
    {
        if (change is not { } c)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var s = Text;
        (Guid? Container, bool Audio) info;
        lock (_gate)
        {
            _info.TryGetValue(c.Id, out info);
        }

        var glyph = info.Audio ? NoticeGlyphs.Headphones : NoticeGlyphs.Bluetooth;
        var key = "bt:" + c.Name;   // keyed by name so the twins (classic/LE) share the same slot
        if (!c.Connected)
        {
            Board.Flash(key, SystemNotice.Make(NoticeText.Disconnected(s, c.Name), glyph, now, open: SettingsUri), DisconnectedShow, now);
            return;
        }

        Board.Flash(key, SystemNotice.Make(NoticeText.Connected(s, c.Name), glyph, now, open: SettingsUri), ConnectedShow, now);
        if (info.Container is { } container)
        {
            _ = AddBatteryAsync(key, c.Name, glyph, container, now);
        }
    }

    /// <summary>Finds battery % on devices in the same device group and appends it to the hint line if the connect notice is still shown.</summary>
    private async Task AddBatteryAsync(string key, string name, string glyph, Guid container, DateTimeOffset shownAt)
    {
        try
        {
            await Task.Delay(BatteryDelay).ConfigureAwait(false);
            var aqs = $"System.Devices.ContainerId:=\"{{{container}}}\"";
            var devices = await DeviceInformation.FindAllAsync(aqs, [BatteryKey], DeviceInformationKind.Device);
            int? percent = null;
            foreach (var device in devices)
            {
                if (device.Properties.TryGetValue(BatteryKey, out var value) && value is byte b and <= 100)
                {
                    percent = b;
                    break;
                }
            }

            var now = DateTimeOffset.UtcNow;
            var left = shownAt + ConnectedShow - now;
            if (percent is not { } p || IsDisposed || left <= TimeSpan.Zero || !Board.Has(key, now))
            {
                return;
            }

            var s = Text;
            Board.Flash(key, SystemNotice.Make(
                NoticeText.Connected(s, name), glyph, shownAt, msg: NoticeText.BatteryLevel(s, p),
                open: SettingsUri), left, now);   // no gauge — the battery level is in the text (user 10-02)
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Many devices/drivers can't report battery — omit it if missing
        }
    }

    private static bool? ReadBool(IReadOnlyDictionary<string, object> properties, string key)
        => properties.TryGetValue(key, out var value) && value is bool b ? b : null;

    private static Guid? ReadGuid(IReadOnlyDictionary<string, object> properties, string key)
        => properties.TryGetValue(key, out var value) && value is Guid g ? g : null;

    private static uint? ReadUInt(IReadOnlyDictionary<string, object> properties, string key)
        => properties.TryGetValue(key, out var value) switch
        {
            true when value is uint u => u,
            true when value is byte b => b,
            true when value is int i and >= 0 => (uint)i,
            _ => null,
        };
}
