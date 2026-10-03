using IsleBar.App.Interop;
using IsleBar.Core.SystemWatch;
using Microsoft.Win32;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Turns Windows' own notification pop-ups (banners) off per app while the bar shows them, and back on afterwards — the registry side
/// of <see cref="ToastBannerPlan"/>. The same switch as Settings › Notifications › {app} › "Show notification banners", so the
/// notification still reaches the notification center and the bar (measured on PC 10-01). The original values are kept in a ledger
/// file, so a crash or a restart doesn't lose them: the next start restores (option off) or keeps going (option on).
/// </summary>
internal static class ToastBanners
{
    private const string SettingsKey = @"Software\Microsoft\Windows\CurrentVersion\Notifications\Settings";
    private const string ShowBanner = "ShowBanner";
    private static readonly object Gate = new();
    private static volatile bool _wanted;   // the latest wish; Hide/Restore check it under the lock so a late job can't undo a newer one

    /// <summary>Records whether banners should be hidden. Call before queuing Hide/Restore.</summary>
    public static void Want(bool hide) => _wanted = hide;

    private static string LedgerFile => Path.Combine(AppPaths.DataDirectory, "toast_banners.json");

    /// <summary>Silences every app that still shows banners (also apps that appeared since the last call). Never throws.</summary>
    public static void Hide()
    {
        lock (Gate)
        {
            if (!_wanted)
            {
                return;   // turned off meanwhile (a queued Hide running after the Restore)
            }

            try
            {
                using var root = Registry.CurrentUser.OpenSubKey(SettingsKey, writable: true);
                if (root is null)
                {
                    return;
                }

                var current = new Dictionary<string, int?>(StringComparer.OrdinalIgnoreCase);
                CollectApps(root, "", current, depth: 0);

                var ledger = ReadLedger();
                var hide = ToastBannerPlan.ToHide(current, ledger);
                if (hide.Count == 0)
                {
                    return;
                }

                foreach (var (app, original) in hide)
                {
                    ledger[app] = original;   // record first, so a failure halfway still restores what was touched
                }

                WriteLedger(ledger);
                foreach (var app in hide.Keys)
                {
                    using var key = root.OpenSubKey(app, writable: true);
                    key?.SetValue(ShowBanner, 0, RegistryValueKind.DWord);
                }

                AppLog.Write($"toast banners hidden: {hide.Count} app(s)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                AppLog.Write("toast banners hide failed: " + ex.Message);
            }
        }
    }

    /// <summary>Puts back every app we silenced (skipping ones the user changed since), then forgets the ledger. Never throws.</summary>
    public static void Restore()
    {
        lock (Gate)
        {
            if (_wanted || !File.Exists(LedgerFile))
            {
                return;
            }

            try
            {
                var ledger = ReadLedger();
                using var root = Registry.CurrentUser.OpenSubKey(SettingsKey, writable: true);
                foreach (var (app, original) in ledger)
                {
                    using var key = root?.OpenSubKey(app, writable: true);
                    if (key is null || !ToastBannerPlan.ShouldRestore(key.GetValue(ShowBanner) as int?))
                    {
                        continue;
                    }

                    if (original is { } value)
                    {
                        key.SetValue(ShowBanner, value, RegistryValueKind.DWord);
                    }
                    else
                    {
                        key.DeleteValue(ShowBanner, throwOnMissingValue: false);
                    }
                }

                File.Delete(LedgerFile);
                AppLog.Write($"toast banners restored: {ledger.Count} app(s)");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
            {
                AppLog.Write("toast banners restore failed: " + ex.Message);
            }
        }
    }

    /// <summary>
    /// App keys under the notification settings. An app ID containing '\' (e.g. PowerShell's
    /// "{1AC14E77-…}\WindowsPowerShell\v1.0\powershell.exe") is stored as nested keys, and only the innermost one is the app —
    /// the switch set on the outer key does nothing (measured on PC 10-01). So: a key with values, or with no sub-keys, is an app.
    /// </summary>
    private static void CollectApps(RegistryKey parent, string prefix, Dictionary<string, int?> apps, int depth)
    {
        foreach (var name in parent.GetSubKeyNames())
        {
            using var key = parent.OpenSubKey(name);
            if (key is null)
            {
                continue;
            }

            var path = prefix.Length == 0 ? name : prefix + "\\" + name;
            if (key.ValueCount > 0 || key.SubKeyCount == 0)
            {
                apps[path] = key.GetValue(ShowBanner) as int?;
            }

            if (key.SubKeyCount > 0 && depth < 6)
            {
                CollectApps(key, path, apps, depth + 1);
            }
        }
    }

    private static Dictionary<string, int?> ReadLedger()
    {
        try
        {
            return ToastBannerPlan.Parse(File.Exists(LedgerFile) ? File.ReadAllText(LedgerFile) : null);
        }
        catch (IOException)
        {
            return ToastBannerPlan.Parse(null);
        }
    }

    private static void WriteLedger(IReadOnlyDictionary<string, int?> ledger)
        => File.WriteAllText(LedgerFile, ToastBannerPlan.Serialize(ledger));
}
