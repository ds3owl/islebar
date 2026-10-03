using System.Diagnostics;
using IsleBar.Core.Island;
using IsleBar.Core.Localization;
using IsleBar.Core.SystemWatch;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Common skeleton for system notice watchers. Keeps the same contract as <see cref="Media.DownloadWatcher"/>:
/// started with <see cref="Start"/>, stopped with <see cref="Dispose"/>, <see cref="Current"/> is readable <b>from any thread</b>,
/// and whatever happens in the background, it <b>never throws outward</b> — Windows events come from the thread pool,
/// and an exception leaking there kills the whole app.
/// Strings are fetched fresh via <see cref="Text"/> on every call — changing the language in settings applies from the next notice.
/// </summary>
internal abstract class NoticeWatcher : IDisposable
{
    private readonly Func<LanguageStrings> _strings;
    private int _disposed;

    protected NoticeWatcher(Func<LanguageStrings> strings)
    {
        _strings = strings ?? throw new ArgumentNullException(nameof(strings));
    }

    /// <summary>Notices to show now. Expired transient notices are excluded. Safe to read from any thread.</summary>
    public IReadOnlyList<ActivityState> Current => Board.Snapshot(DateTimeOffset.UtcNow);

    protected NoticeBoard Board { get; } = new();

    /// <summary>Strings for the current language. English if fetching fails.</summary>
    protected LanguageStrings Text
    {
        get
        {
            try
            {
                return _strings();
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                return LanguageCatalog.For(LanguageCatalog.Fallback);
            }
        }
    }

    protected bool IsDisposed => Volatile.Read(ref _disposed) != 0;

    /// <summary>Starts watching. Calling twice starts it only once (implementation's responsibility). Never throws.</summary>
    public abstract void Start();

    /// <summary>
    /// Wraps an event handler. Never lets any exception out — after one failure the next event can simply retry,
    /// and missing one notice is better than the island going down because of one notice.
    /// </summary>
    // timer and event threads report at the same time — a plain HashSet could throw inside the catch and take the bar down
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _loggedErrors = new();

    protected void Guard(Action action)
    {
        if (IsDisposed)
        {
            return;
        }

        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // To the app log too (once per distinct error): a watcher that throws every scan used to fail silently —
            // the mic/camera dot never lit and nothing said why (10-01).
            var line = $"[{GetType().Name}] {ex.GetType().Name}: {ex.Message}";
            Debug.WriteLine(line);
            if (_loggedErrors.TryAdd(line, 0))
            {
                Interop.TaskbarHost.Log("watcher error " + line + Environment.NewLine + ex.StackTrace);
            }
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        try
        {
            Stop();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Debug.WriteLine($"[{GetType().Name}] stop: {ex.Message}");
        }

        Board.ClearAll();
    }

    /// <summary>Unsubscribes and cleans up threads. Called only once by <see cref="Dispose"/>.</summary>
    protected abstract void Stop();
}
