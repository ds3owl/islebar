using IsleBar.Core.Configuration;

namespace IsleBar.Core.SystemWatch;

/// <summary>
/// Which system notices to turn on. Extracted from settings (<see cref="IsleBarSettings"/>) and passed to the app's watcher bundle —
/// so the watchers do not need to know the settings file's shape.
/// </summary>
public sealed record SystemWatchOptions(
    bool Power = true,
    bool Bluetooth = true,
    bool Network = true,
    bool Focus = true,
    bool Clipboard = true,
    bool Load = true,
    bool Privacy = true,
    string CalendarIcs = "",
    bool Toasts = false,
    bool HideBanners = false,
    bool Updates = true)
{
    /// <summary>Everything off.</summary>
    public static SystemWatchOptions None { get; } = new(false, false, false, false, false, false, false, "", Toasts: false, HideBanners: false, Updates: false);

    /// <summary>Calendar notices are on only when a URL is set.</summary>
    public bool Calendar => !string.IsNullOrWhiteSpace(CalendarIcs);

    public static SystemWatchOptions From(IsleBarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SystemWatchOptions(
            settings.NotifyPower,
            settings.NotifyBluetooth,
            settings.NotifyNetwork,
            settings.NotifyFocus,
            settings.NotifyClipboard,
            settings.NotifyLoad,
            settings.NotifyPrivacy,
            settings.CalendarIcs?.Trim() ?? "",
            settings.NotifyToasts,
            settings.NotifyToasts && settings.HideToastBanners,   // hiding Windows' banners only makes sense while the bar shows them
            settings.CheckUpdates);
    }
}
