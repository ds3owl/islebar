using IsleBar.Core.SystemWatch;
using Windows.System.Power;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Charger plugged/unplugged and low battery (<see cref="PowerManager"/>). Plugging in shows "Charging · 78%" for 2.5 s, unplugging "Battery · 76%" for 2 s,
/// and at ≤20% without a charger "Battery 20% · about 45 min" until plugged in or sufficiently charged (red border).
/// On desktops without a battery nothing is shown. Rules are in <see cref="PowerLogic"/>.
/// PowerManager events come from the thread pool — no UI thread needed.
/// Even if events are missed (right after waking from sleep, etc.), re-reads every 30 s to keep the low warning and remaining time right.
/// </summary>
internal sealed class PowerWatcher : NoticeWatcher
{
    private const string PluggedKey = "power";
    private const string LowKey = "power-low";
    private const string SettingsUri = "ms-settings:batterysaver";
    private static readonly TimeSpan PluggedShow = TimeSpan.FromSeconds(2.5);
    private static readonly TimeSpan UnpluggedShow = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(30);

    private readonly object _gate = new();
    private readonly PowerLogic _logic = new();
    private Timer? _poll;
    private int? _lowLevel;
    private DateTimeOffset _lowSince;
    private bool _started;

    public PowerWatcher(Func<Core.Localization.LanguageStrings> strings)
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
        PowerManager.BatteryStatusChanged += OnChanged;
        PowerManager.PowerSupplyStatusChanged += OnChanged;
        PowerManager.RemainingChargePercentChanged += OnChanged;
        PowerManager.RemainingDischargeTimeChanged += OnChanged;
        _poll = new Timer(_ => Guard(Refresh), null, TimeSpan.Zero, PollInterval);
    });

    protected override void Stop()
    {
        if (!_started)
        {
            return;
        }

        PowerManager.BatteryStatusChanged -= OnChanged;
        PowerManager.PowerSupplyStatusChanged -= OnChanged;
        PowerManager.RemainingChargePercentChanged -= OnChanged;
        PowerManager.RemainingDischargeTimeChanged -= OnChanged;
        _poll?.Dispose();
    }

    private void OnChanged(object? sender, object e) => Guard(Refresh);

    private void Refresh()
    {
        var reading = Read(out var remaining);
        var now = DateTimeOffset.UtcNow;
        var s = Text;
        lock (_gate)
        {
            switch (_logic.Observe(reading))
            {
                case PowerChange.PluggedIn:
                    Board.Flash(PluggedKey, SystemNotice.Make(
                        NoticeText.Charging(s, reading.Percent), NoticeGlyphs.Charging, now,
                        open: SettingsUri), PluggedShow, now);   // no gauge — the percent is in the text (user 10-01)
                    break;
                case PowerChange.Unplugged:
                    Board.Flash(PluggedKey, SystemNotice.Make(
                        NoticeText.OnBattery(s, reading.Percent), NoticeGlyphs.Battery(reading.Percent), now,
                        open: SettingsUri), UnpluggedShow, now);
                    break;
            }

            var level = PowerLogic.LowLevel(reading);
            if (level is null)
            {
                _lowLevel = null;
                Board.Clear(LowKey);
                return;
            }

            if (level != _lowLevel)
            {
                _lowLevel = level;
                _lowSince = now;   // dropping from 20 → 10 is a new notice (change the timestamp so the island notices again)
            }

            // Percent and remaining time are rewritten every time, but the timestamp stays at when the threshold was crossed — so it doesn't look like a "new notice" every 30 s
            Board.Hold(LowKey, SystemNotice.Make(
                NoticeText.BatteryLow(s, reading.Percent, remaining), NoticeGlyphs.Battery(reading.Percent), _lowSince,
                urgent: true, open: SettingsUri));   // no gauge — the percent is already in the text (user 10-01)
        }
    }

    private static PowerReading Read(out TimeSpan? remaining)
    {
        remaining = null;
        var hasBattery = PowerManager.BatteryStatus != BatteryStatus.NotPresent;
        var plugged = PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent;
        var percent = PowerManager.RemainingChargePercent;
        if (hasBattery && !plugged)
        {
            remaining = PowerManager.RemainingDischargeTime;   // a huge value if unknown → NoticeText discards it
        }

        return new PowerReading(hasBattery, plugged, percent);
    }
}
