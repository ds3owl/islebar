using IsleBar.Core.Island;

namespace IsleBar.Core.SystemWatch;

/// <summary>
/// The notices one system watcher (charging, Bluetooth, internet, etc.) currently wants to put on the island.
/// Watcher events arrive on thread-pool/dedicated threads while the island reads on the UI thread, so it is <b>guarded by a lock</b>.
/// Each notice has a key, and posting again with the same key replaces it — so plugging the charger in and out repeatedly does not pile up on the island.
/// Time is passed in from outside (so tests can advance time).
/// </summary>
public sealed class NoticeBoard
{
    private readonly object _gate = new();
    private readonly List<Entry> _items = [];

    /// <summary>
    /// A notice shown for only a few seconds. It drops out of <see cref="Snapshot"/> after <paramref name="duration"/>.
    /// </summary>
    public void Flash(string key, ActivityState notice, TimeSpan duration, DateTimeOffset now)
        => Put(key, notice, now + duration);

    /// <summary>A notice that stays until cleared (<see cref="Clear"/>) — for when a human needs to act, like low battery or internet lost.</summary>
    public void Hold(string key, ActivityState notice) => Put(key, notice, null);

    /// <summary>Takes down the notice with that key. Does nothing if absent.</summary>
    public void Clear(string key)
    {
        lock (_gate)
        {
            _items.RemoveAll(e => e.Key == key);
        }
    }

    /// <summary>
    /// Replaces all notices at once (all persistent). For watchers that recompute from scratch each time (calendar) —
    /// if the island read between clearing and re-posting it would look empty for an instant and flicker, so it is swapped at once inside the lock.
    /// </summary>
    public void ReplaceAll(IEnumerable<KeyValuePair<string, ActivityState>> notices)
    {
        ArgumentNullException.ThrowIfNull(notices);
        var entries = new List<Entry>();
        foreach (var (key, notice) in notices)
        {
            notice.SourcePath ??= SystemNotice.SourcePrefix + key;
            entries.RemoveAll(e => e.Key == key);
            entries.Add(new Entry(key, notice, null));
        }

        lock (_gate)
        {
            _items.Clear();
            _items.AddRange(entries);
        }
    }

    /// <summary>Takes down all notices (when the watcher is turned off).</summary>
    public void ClearAll()
    {
        lock (_gate)
        {
            _items.Clear();
        }
    }

    /// <summary>Whether a notice with that key is up (expired ones count as absent).</summary>
    public bool Has(string key, DateTimeOffset now)
    {
        lock (_gate)
        {
            return _items.Any(e => e.Key == key && (e.Until is null || e.Until > now));
        }
    }

    /// <summary>Notices to show now (in posting order). Expired ones are removed at this point. Safe to call from any thread.</summary>
    public IReadOnlyList<ActivityState> Snapshot(DateTimeOffset now)
    {
        lock (_gate)
        {
            _items.RemoveAll(e => e.Until is { } until && until <= now);
            return _items.Count == 0 ? [] : _items.Select(e => e.Notice).ToArray();
        }
    }

    private void Put(string key, ActivityState notice, DateTimeOffset? until)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(notice);
        notice.SourcePath ??= SystemNotice.SourcePrefix + key;
        lock (_gate)
        {
            _items.RemoveAll(e => e.Key == key);
            _items.Add(new Entry(key, notice, until));
        }
    }

    private sealed record Entry(string Key, ActivityState Notice, DateTimeOffset? Until);
}

/// <summary>
/// Builds a single short notice. Collected here so every watcher builds them the same way —
/// the island draws based only on <see cref="ActivityState.Kind"/> = <see cref="ActivityKind.Notice"/>.
/// </summary>
public static class SystemNotice
{
    /// <summary>
    /// Prefix of a notice's <see cref="ActivityState.SourcePath"/>. The island uses it as the key to tell items apart,
    /// so it must not collide with other sources (<c>download:</c> etc.).
    /// </summary>
    public const string SourcePrefix = "system:";

    /// <summary>
    /// One notice. If <paramref name="urgent"/>, State=error (orange border) — only turn it on when a human must do something to resolve it.
    /// Given <paramref name="done"/>/<paramref name="total"/>, the island draws the ratio as a bar/ring (battery % etc.).
    /// </summary>
    public static ActivityState Make(
        string title,
        string glyph,
        DateTimeOffset now,
        string? msg = null,
        bool urgent = false,
        long? done = null,
        long? total = null,
        string? open = null)
        => new()
        {
            RawKind = ActivityState.KindNotice,
            Title = title,
            Msg = string.IsNullOrWhiteSpace(msg) ? null : msg,
            Glyph = glyph,
            State = urgent ? "error" : "run",
            Done = done,
            Total = total,
            Open = open,
            T0 = now.ToUnixTimeMilliseconds() / 1000.0,
            UpdatedAt = now,
        };
}

/// <summary>
/// Notice icons (Segoe Fluent Icons character codes). Same font as the island's other icons.
/// All codes below were confirmed present in the SegoeIcons.ttf character map on PC (09-30). However, whether the shapes match their meaning
/// has not yet been checked by eye for <see cref="NoInternet"/> (F384), <see cref="Image"/> (EB9F) and <see cref="Load"/> (E950).
/// </summary>
public static class NoticeGlyphs
{
    /// <summary>Lightning (LightningBolt) — charging.</summary>
    public const string Charging = "";

    /// <summary>Empty battery (Battery0). <see cref="Battery"/> picks 1–10 bars in 10% steps.</summary>
    public const string Battery0 = "";

    public const string Bluetooth = "";
    public const string Headphones = "";
    public const string Wifi = "";

    /// <summary>NetworkOffline (Fluent Icons). Not in MDL2.</summary>
    public const string NoInternet = "";

    /// <summary>Moon (QuietHours) — focus session.</summary>
    public const string Focus = "";

    public const string Copy = "";
    public const string Image = "";
    public const string Files = "";

    /// <summary>Diagnostic — CPU/memory overload (no fitting dedicated icon, so the diagnostic icon is used).</summary>
    public const string Load = "";

    /// <summary>Download (arrow into a tray) — a newer IsleBar is out (10-01). Same glyph as incoming transfers.</summary>
    public const string Download = "";

    public const string Calendar = "";
    public const string Microphone = "";
    public const string Camera = "";

    /// <summary>Battery bar icon. 0–100% → Battery0–Battery10 (EBA0–EBAA).</summary>
    public static string Battery(int percent)
        => ((char)(Battery0[0] + Math.Clamp((percent + 5) / 10, 0, 10))).ToString();
}
