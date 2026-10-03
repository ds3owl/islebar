using IsleBar.Core.Configuration;

namespace IsleBar.Core.Models;

/// <summary>The side that fetches the docs body. Tests supply a fixed file.</summary>
public interface IModelAliasSource
{
    Task<string?> FetchAsync(CancellationToken cancellationToken = default);
}

/// <summary>Result of an alias update.</summary>
/// <param name="Checked">Whether a check actually happened (false if 6 hours have not passed yet).</param>
/// <param name="Changed">Whether the list changed — if true, the buttons must be redrawn.</param>
/// <param name="Models">The list after the update.</param>
/// <param name="Saved">Whether it was saved to the settings file.</param>
public sealed record ModelAliasUpdate(bool Checked, bool Changed, IReadOnlyList<string> Models, bool Saved);

/// <summary>
/// Checks the alias list once at startup and every 6 hours after.
/// If the fetch fails or looks wrong, the list is left alone (buttons disappearing is the worst case).
/// <b>If saving fails, Changed is not reported</b> — a restart would still see the same list,
/// leading to an endless restart loop (the same reason we hit in the Python version).
/// </summary>
public sealed class ModelAliasUpdater(ConfigStore store, IModelAliasSource source)
{
    /// <summary>Check interval.</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromHours(6);

    private readonly ConfigStore _store = store ?? throw new ArgumentNullException(nameof(store));
    private readonly IModelAliasSource _source = source ?? throw new ArgumentNullException(nameof(source));

    /// <summary>Whether it is time to check.</summary>
    public static bool IsDue(double lastCheckedUnix, DateTimeOffset now)
        => now.ToUnixTimeSeconds() - lastCheckedUnix >= Interval.TotalSeconds;

    /// <summary>
    /// Checks and updates the list if needed.
    /// </summary>
    /// <param name="now">Current time (injected in tests).</param>
    /// <param name="force">Check regardless of the interval.</param>
    public async Task<ModelAliasUpdate> RunOnceAsync(
        DateTimeOffset now, bool force = false, CancellationToken cancellationToken = default)
    {
        var settings = _store.Load();
        if (!force && !IsDue(settings.ModelsChecked, now))
        {
            return new ModelAliasUpdate(Checked: false, Changed: false, settings.Models, Saved: false);
        }

        var markdown = await _source.FetchAsync(cancellationToken).ConfigureAwait(false);
        var found = ModelAliasParser.Parse(markdown);

        var changed = false;
        IReadOnlyList<string> resulting = settings.Models;
        var saved = _store.Update(current =>
        {
            current.ModelsChecked = now.ToUnixTimeSeconds();
            if (found is not null)
            {
                var merged = ModelAliasParser.Merge(current.Models, found);
                if (!merged.SequenceEqual(current.Models, StringComparer.Ordinal))
                {
                    current.Models = [.. merged];
                    changed = true;
                }
            }

            resulting = current.Models;
            return true;
        });

        // if it could not be written, do not report a change (a restart would see the same state → endless restarts)
        return new ModelAliasUpdate(Checked: true, Changed: changed && saved, resulting, saved);
    }
}
