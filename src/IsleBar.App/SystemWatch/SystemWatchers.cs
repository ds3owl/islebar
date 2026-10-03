using IsleBar.Core.Island;
using IsleBar.Core.Localization;
using IsleBar.Core.SystemWatch;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Bundle of system notice watchers. The island (MainWindow) holds only this,
/// merges notices via <c>items.AddRange(_system.Current)</c>, and reads <see cref="MicInUse"/> etc. for the mic/camera indicator.
/// <para>
/// Threads: every watcher runs on its own background thread (thread pool / dedicated thread) — <b>none needs the UI thread.</b>
/// <see cref="Start"/>/<see cref="Apply"/>/<see cref="Dispose"/> may be called from any thread (guarded against each other by a lock),
/// and <see cref="Current"/> and the privacy properties are safe to read from any thread. No method throws.
/// </para>
/// When settings change, call <see cref="Apply"/> again — it starts only newly enabled ones and stops only disabled ones (if the calendar URL changes, only the calendar restarts).
/// </summary>
internal sealed class SystemWatchers : IDisposable
{
    private readonly object _gate = new();
    private readonly Func<LanguageStrings> _strings;
    private PowerWatcher? _power;
    private BluetoothWatcher? _bluetooth;
    private NetworkWatcher? _network;
    private FocusWatcher? _focus;
    private ClipboardWatcher? _clipboard;
    private LoadWatcher? _load;
    private CalendarWatcher? _calendar;
    private PrivacyWatcher? _privacy;
    private ToastWatcher? _toasts;
    private UpdateWatcher? _updates;
    private AgentRecordWatcher? _codex;   // always on: a Codex turn that fails and a Claude turn interrupted with Esc fire no hook (10-03)
    private bool? _hidingBanners;   // null until the first Apply (which also cleans up after a crash with the option off)
    private bool _disposed;

    /// <param name="strings">Function that returns strings for the current language (changing the language in settings applies from the next notice).</param>
    public SystemWatchers(Func<LanguageStrings> strings)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
    }

    /// <summary>Initial start. Same as <see cref="Apply"/>.</summary>
    public void Start(SystemWatchOptions options) => Apply(options);

    /// <summary>Starts and stops watchers per settings. Safe to call repeatedly.</summary>
    public void Apply(SystemWatchOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            Toggle(ref _power, options.Power, () => new PowerWatcher(_strings));
            Toggle(ref _bluetooth, options.Bluetooth, () => new BluetoothWatcher(_strings));
            Toggle(ref _network, options.Network, () => new NetworkWatcher(_strings));
            Toggle(ref _focus, options.Focus, () => new FocusWatcher(_strings));
            Toggle(ref _clipboard, options.Clipboard, () => new ClipboardWatcher(_strings));
            Toggle(ref _load, options.Load, () => new LoadWatcher(_strings));
            Toggle(ref _privacy, options.Privacy, () => new PrivacyWatcher(_strings));
            Toggle(ref _toasts, options.Toasts, () => new ToastWatcher(_strings));
            Toggle(ref _updates, options.Updates, () => new UpdateWatcher(_strings));
            Toggle(ref _codex, true, () => new AgentRecordWatcher(_strings));
            if (_toasts is not null)
            {
                _toasts.HideBanners = options.HideBanners;
            }

            // Only hide Windows' pop-ups when the bar can actually read the notifications — without the database the person would
            // see no notifications at all (code review 10-01)
            ApplyBanners(options.HideBanners && _toasts is not null && File.Exists(ToastWatcher.DatabasePath), wait: false);

            // The calendar must refetch when its URL changes, so stop and restart it
            if (_calendar is not null && (!options.Calendar || _calendar.Url != CalendarWatcher.NormalizeUrl(options.CalendarIcs)))
            {
                _calendar.Dispose();
                _calendar = null;
            }

            Toggle(ref _calendar, options.Calendar, () => new CalendarWatcher(_strings, options.CalendarIcs));
        }
    }

    /// <summary>All notices from enabled watchers concatenated. Safe to read from any thread.</summary>
    public IReadOnlyList<ActivityState> Current
    {
        get
        {
            NoticeWatcher?[] watchers;
            lock (_gate)
            {
                watchers = [_power, _bluetooth, _network, _focus, _clipboard, _load, _calendar, _privacy, _toasts, _updates, _codex];
            }

            List<ActivityState>? all = null;
            foreach (var watcher in watchers)
            {
                if (watcher?.Current is { Count: > 0 } items)
                {
                    (all ??= []).AddRange(items);
                }
            }

            return all is null ? [] : all;
        }
    }

    /// <summary>Microphone in use (always false if privacy watching is off).</summary>
    public bool MicInUse => _privacy?.MicInUse ?? false;

    /// <summary>Camera in use (always false if privacy watching is off).</summary>
    public bool CameraInUse => _privacy?.CameraInUse ?? false;

    /// <summary>Name of the app using the mic/camera. Null if none.</summary>
    public string? PrivacyApp => _privacy?.AppName;

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Toggle(ref _power, false, null!);
            Toggle(ref _bluetooth, false, null!);
            Toggle(ref _network, false, null!);
            Toggle(ref _focus, false, null!);
            Toggle(ref _clipboard, false, null!);
            Toggle(ref _load, false, null!);
            Toggle(ref _calendar, false, null!);
            Toggle(ref _privacy, false, null!);
            Toggle(ref _toasts, false, null!);
            Toggle(ref _updates, false, null!);
            Toggle(ref _codex, false, null!);
            ApplyBanners(false, wait: true);   // quitting the bar gives Windows its pop-ups back (nothing would show the notifications otherwise)
        }
    }

    /// <summary>Windows' own pop-ups off while the bar shows notifications (option "only on the bar"), back on otherwise.</summary>
    /// <param name="wait">Do it now (on quit — a queued job would die with the process) instead of off the calling thread.</param>
    private void ApplyBanners(bool hide, bool wait)
    {
        if (_hidingBanners == hide)
        {
            return;
        }

        _hidingBanners = hide;
        ToastBanners.Want(hide);
        Action work = hide ? ToastBanners.Hide : ToastBanners.Restore;
        if (wait)
        {
            work();
        }
        else
        {
            ThreadPool.QueueUserWorkItem(_ => work());
        }
    }

    private static void Toggle<T>(ref T? watcher, bool on, Func<T> create)
        where T : NoticeWatcher
    {
        if (on && watcher is null)
        {
            watcher = create();
            watcher.Start();
        }
        else if (!on && watcher is not null)
        {
            watcher.Dispose();
            watcher = null;
        }
    }
}
