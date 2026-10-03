using IsleBar.Core.Island;
using Windows.Media.Control;

namespace IsleBar.App.Media;

/// <summary>
/// Turns what's playing now (Windows media controls — what Spotify, browser YouTube, etc. report)
/// into an island item. Nothing is written to disk; memory only (re-read every second).
/// Shown only while playing — pausing removes it from the island.
/// </summary>
internal sealed class MediaWatcher : IDisposable
{
    // Event-driven: track/state/timeline changes wake the loop instantly (SafetyPoll is only a slow backstop for missed events
    // and reconnection). A 1 s poll used to make a track change or a play/pause button press show up to ~1 s late (09-30 → 10-01).
    private static readonly TimeSpan SafetyPoll = TimeSpan.FromSeconds(1);   // also the playback-position refresh cadence for the progress bar (track/state changes come from events)

    private readonly CancellationTokenSource _stop = new();
    private readonly SemaphoreSlim _wake = new(0, 1);

    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, PlaybackInfoChangedEventArgs> _onPlayback;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSession, MediaPropertiesChangedEventArgs> _onProperties;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, CurrentSessionChangedEventArgs> _onSession;
    private readonly Windows.Foundation.TypedEventHandler<GlobalSystemMediaTransportControlsSessionManager, SessionsChangedEventArgs> _onSessions;

    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _subscribed;

    public MediaWatcher()
    {
        _onPlayback = (_, _) => SignalRefresh();
        _onProperties = (_, _) => SignalRefresh();
        _onSession = (_, _) => SignalRefresh();
        _onSessions = (_, _) => SignalRefresh();
    }

    /// <summary>The music item to show now. Null if nothing is playing. Safe to read from any thread.</summary>
    public ActivityState? Current { get; private set; }

    /// <summary>Album art for the current track (only if the player provides it). When the track changes, <see cref="CurrentArtKey"/> changes too.</summary>
    public Windows.Storage.Streams.IRandomAccessStreamReference? CurrentArt { get; private set; }

    public string? CurrentArtKey { get; private set; }

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>Wake the loop to re-read now (from a media event or a button press). Coalesced — extra signals collapse into one read.</summary>
    private void SignalRefresh()
    {
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // already pending — one re-read covers it
        }
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                if (_manager is null)
                {
                    _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    _manager.CurrentSessionChanged += _onSession;
                    _manager.SessionsChanged += _onSessions;
                }

                var session = PickMusicSession(_manager);
                Subscribe(session);
                var read = await ReadAsync(session);
                // Players briefly blank the track info at moments (track skip, info refresh) — each time the island turned off and on, and the scrolling title
                // jumped back to the start and looked stuck (measured on PC 09-30). Ignore blanks shorter than 3 s and keep the previous track.
                if (read is null && Current is not null && DateTimeOffset.UtcNow - _lastSeen < TimeSpan.FromSeconds(3))
                {
                    // keep the previous track
                }
                else
                {
                    Current = read;
                    if (read is not null)
                    {
                        _lastSeen = DateTimeOffset.UtcNow;
                    }
                }
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // any failure (a session closing mid-read throws all sorts): start over — an uncaught one ended this loop and left
                // the last track on the pill for good (review 10-03)
                Current = null;
                try
                {
                    Unsubscribe();   // on a session that just closed this can throw too
                }
                catch (Exception unsubscribeEx) when (unsubscribeEx is not OutOfMemoryException)
                {
                    _subscribed = null;
                }

                if (_manager is { } stale)
                {
                    try
                    {
                        // the old manager kept calling these after it was dropped, and every reconnect added another pair (review 10-03)
                        stale.CurrentSessionChanged -= _onSession;
                        stale.SessionsChanged -= _onSessions;
                    }
                    catch (Exception unhookEx) when (unhookEx is not OutOfMemoryException)
                    {
                    }
                }

                _manager = null;   // reconnect on the next wake
            }

            try
            {
                await _wake.WaitAsync(SafetyPoll, _stop.Token);   // wake on a media event, or re-read after the safety interval
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>Subscribe to the picked session's change events so a track/state/position change wakes the loop at once.</summary>
    private void Subscribe(GlobalSystemMediaTransportControlsSession? session)
    {
        if (ReferenceEquals(session, _subscribed))
        {
            return;
        }

        Unsubscribe();
        _subscribed = session;
        if (session is not null)
        {
            session.PlaybackInfoChanged += _onPlayback;
            session.MediaPropertiesChanged += _onProperties;
        }
    }

    private void Unsubscribe()
    {
        if (_subscribed is not null)
        {
            _subscribed.PlaybackInfoChanged -= _onPlayback;
            _subscribed.MediaPropertiesChanged -= _onProperties;
            _subscribed = null;
        }
    }

    /// <summary>Keep showing this long after pausing — so it can be resumed with the island's ⏯ button.</summary>
    private static readonly TimeSpan PausedLinger = TimeSpan.FromSeconds(30);

    private DateTimeOffset _lastPlaying = DateTimeOffset.MinValue;
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;

    /// <summary>
    /// Picks only playback from music programs (browser playback is excluded — user feedback 09-30). Even if Windows' "current" session is a browser,
    /// if Spotify is playing in the background, show that: a playing music app → otherwise the current session (only if it's a music app).
    /// </summary>
    private static GlobalSystemMediaTransportControlsSession? PickMusicSession(GlobalSystemMediaTransportControlsSessionManager manager)
    {
        var music = manager.GetSessions().Where(s => !MediaSources.IsBrowser(s.SourceAppUserModelId)).ToList();
        return music.FirstOrDefault(s => s.GetPlaybackInfo()?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
               ?? (manager.GetCurrentSession() is { } current && !MediaSources.IsBrowser(current.SourceAppUserModelId) ? current : null)
               ?? music.FirstOrDefault();
    }

    private async Task<ActivityState?> ReadAsync(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session?.GetPlaybackInfo() is not { } playback)
        {
            return null;
        }

        var playing = playback.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
        if (playing)
        {
            _lastPlaying = DateTimeOffset.UtcNow;
        }
        else if (DateTimeOffset.UtcNow - _lastPlaying > PausedLinger)
        {
            return null;
        }

        var props = await session.TryGetMediaPropertiesAsync();
        CurrentArt = props?.Thumbnail;
        CurrentArtKey = props is null ? null : $"{session.SourceAppUserModelId}|{props.Title}|{props.AlbumTitle}";
        var title = props?.Title?.Trim() ?? string.Empty;
        var artist = props?.Artist?.Trim() ?? string.Empty;
        if (title.Length == 0)
        {
            return null;
        }

        // Playback position → bar (same progress bar as the timer). Apps report position only occasionally, so add the time elapsed since the last report.
        long? total = null, done = null;
        var timeline = session.GetTimelineProperties();
        var length = timeline.EndTime - timeline.StartTime;
        if (length > TimeSpan.Zero)
        {
            var position = timeline.Position - timeline.StartTime;
            if (playing)
            {
                position += DateTimeOffset.UtcNow - timeline.LastUpdatedTime;
            }

            total = (long)length.TotalMilliseconds;
            done = (long)Math.Clamp(position.TotalMilliseconds, 0, length.TotalMilliseconds);
        }

        return new ActivityState
        {
            RawKind = ActivityState.KindMusic,
            Total = total,
            Done = done,
            // Track title (bold) · artist (dim) — same as the preview. Long text scrolls once to the end and stops, so the artist is included too (user feedback 09-30)
            Name = title,
            Msg = artist.Length > 0 ? artist : null,
            State = "run",
            Stage = playing ? "playing" : "paused",
            SourcePath = "media:" + session.SourceAppUserModelId,   // used by the island to decide "is it the same item"
            UpdatedAt = DateTimeOffset.UtcNow.AddMinutes(-10),      // sort behind other items (least urgent)
        };
    }

    /// <summary>Play/pause / previous / next. Failures are silently ignored.</summary>
    public async Task SendAsync(MediaAction action)
    {
        try
        {
            // a manager of its own when the loop is between reconnects — setting _manager here left it without its events (review 10-03)
            var manager = _manager ?? await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
            if (PickMusicSession(manager) is not { } session)
            {
                return;
            }

            _ = action switch
            {
                MediaAction.PlayPause => await session.TryTogglePlayPauseAsync(),
                MediaAction.Previous => await session.TrySkipPreviousAsync(),
                _ => await session.TrySkipNextAsync(),
            };

            SignalRefresh();   // reflect the new state/track right away instead of waiting for the event or the safety poll
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException)
        {
        }
    }

    public void Dispose()
    {
        _stop.Cancel();
        try
        {
            if (_manager is not null)
            {
                _manager.CurrentSessionChanged -= _onSession;
                _manager.SessionsChanged -= _onSessions;
            }

            Unsubscribe();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // a session that just closed; quitting must go on (it stopped the rest of the clean-up — review 10-03)
        }

        _stop.Dispose();
        _wake.Dispose();
    }
}

internal enum MediaAction
{
    PlayPause,
    Previous,
    Next,
}
