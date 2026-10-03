using System.Text.Json;

namespace IsleBar.Core.SystemWatch;

/// <summary>
/// "Show Windows notifications only on the bar" (user 10-01: a Windows banner and the bar's card arrived together). Windows keeps a
/// per-app "show notification banners" switch (HKCU\…\Notifications\Settings\{app}\ShowBanner); turning it off hides the pop-up but
/// the notification still lands in the notification center — and in the database the bar reads — so it still shows on the bar
/// (measured on PC 10-01). This decides which apps to switch and remembers each app's original value (a ledger) so turning the
/// option off, or quitting the bar, puts every app back exactly as it was.
/// </summary>
public static class ToastBannerPlan
{
    /// <summary>
    /// Apps whose banners still show (<paramref name="current"/>: app → ShowBanner value, null = not set = shown) and aren't in the
    /// ledger yet, with the value to restore later. Apps the user already silenced (0) are left alone and not recorded.
    /// </summary>
    public static IReadOnlyDictionary<string, int?> ToHide(IReadOnlyDictionary<string, int?> current, IReadOnlyDictionary<string, int?> ledger)
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(ledger);
        var known = new HashSet<string>(ledger.Keys, StringComparer.OrdinalIgnoreCase);   // registry key names ignore case
        var hide = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (app, value) in current)
        {
            if (value != 0 && !known.Contains(app))
            {
                hide[app] = value;
            }
        }

        return hide;
    }

    /// <summary>
    /// Whether to put an app back: only if it is still silenced the way we left it — if the user changed it in Windows Settings
    /// meanwhile, their choice wins.
    /// </summary>
    public static bool ShouldRestore(int? now) => now == 0;

    /// <summary>Ledger file contents (app → original value, null = the value wasn't set).</summary>
    public static string Serialize(IReadOnlyDictionary<string, int?> ledger)
        => JsonSerializer.Serialize(ledger.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).ToDictionary(p => p.Key, p => p.Value));

    /// <summary>Reads a ledger; an empty, missing or broken file gives an empty ledger.</summary>
    public static Dictionary<string, int?> Parse(string? json)
    {
        var ledger = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(json))
        {
            return ledger;
        }

        try
        {
            if (JsonSerializer.Deserialize<Dictionary<string, int?>>(json) is { } read)
            {
                foreach (var (app, value) in read)
                {
                    ledger[app] = value;
                }
            }
        }
        catch (JsonException)
        {
        }

        return ledger;
    }
}
