using IsleBar.Core.SystemWatch;
using Windows.Networking.Connectivity;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Internet disconnect/reconnect. When <see cref="NetworkInformation.NetworkStatusChanged"/> fires, reads the connectivity level
/// of the current internet connection profile (does it reach the internet), and confirms only values that last 3 s (<see cref="ConnectivityLogic"/>) —
/// so the island doesn't flicker on brief drops when switching Wi-Fi. On disconnect shows "Internet disconnected" for 10 s (no border),
/// then "Reconnected" for 2 s. The state at startup isn't notified.
/// Events come from the thread pool and confirmation runs on a 1 s timer — no UI thread needed.
/// Reads directly once every 10 s in case events are missed (resume from sleep, etc.).
/// </summary>
internal sealed class NetworkWatcher : NoticeWatcher
{
    private const string LostKey = "net";
    private const string RestoredKey = "net-ok";
    private const string SettingsUri = "ms-settings:network-status";
    private static readonly TimeSpan RestoredShow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    private const int ReadEveryTicks = 10;

    private readonly object _gate = new();
    private readonly ConnectivityLogic _logic = new();
    private Timer? _tick;
    private int _ticks;
    private bool _started;

    public NetworkWatcher(Func<Core.Localization.LanguageStrings> strings)
        : base(strings)
    {
    }

    public override void Start() => Guard(() =>
    {
        if (_started)
        {
            return;
        }

        _started = true;
        NetworkInformation.NetworkStatusChanged += OnStatusChanged;
        ReportNow();
        _tick = new Timer(_ => Guard(Tick), null, TickInterval, TickInterval);
    });

    protected override void Stop()
    {
        if (!_started)
        {
            return;
        }

        NetworkInformation.NetworkStatusChanged -= OnStatusChanged;
        _tick?.Dispose();
    }

    private void OnStatusChanged(object sender) => Guard(ReportNow);

    private void ReportNow()
    {
        var online = IsOnline();
        lock (_gate)
        {
            _logic.Report(online, DateTimeOffset.UtcNow);
        }
    }

    private void Tick()
    {
        if (++_ticks % ReadEveryTicks == 0)
        {
            ReportNow();
        }

        var now = DateTimeOffset.UtcNow;
        ConnectivityChange change;
        lock (_gate)
        {
            change = _logic.Tick(now);
        }

        var s = Text;
        switch (change)
        {
            case ConnectivityChange.Lost:
                Board.Clear(RestoredKey);
                // a short heads-up, not an orange border for the whole outage — orange means "you need to answer", and an outage
                // (a plane, the provider) usually isn't something the person can fix (user 10-01, same as the CPU notice)
                Board.Flash(LostKey, SystemNotice.Make(s.NoticeOffline, NoticeGlyphs.NoInternet, now, open: SettingsUri), TimeSpan.FromSeconds(10), now);
                break;
            case ConnectivityChange.Restored:
                Board.Clear(LostKey);
                Board.Flash(RestoredKey, SystemNotice.Make(s.NoticeOnline, NoticeGlyphs.Wifi, now, open: SettingsUri), RestoredShow, now);
                break;
        }
    }

    /// <summary>
    /// Whether it reaches the internet. "Local only" and "constrained (public Wi-Fi needing sign-in)" count as disconnected — states where the person has something to do.
    /// </summary>
    private static bool IsOnline()
    {
        var profile = NetworkInformation.GetInternetConnectionProfile();
        return profile?.GetNetworkConnectivityLevel() == NetworkConnectivityLevel.InternetAccess;
    }
}
