using System.Text.Json;

namespace IsleBar.Core.Island;

/// <summary>
/// Reads and writes the folder where state JSON files live.
///
/// Watches two default locations together:
///  - <c>%TEMP%\tgprog\tgprog_*.json</c> — the older progress-file location some scripts still write
///  - <c>%LOCALAPPDATA%\IsleBar\state\*.json</c> — the new integration rule
/// <b>Files unchanged for more than 2 minutes are ignored</b> — so a stale display does not linger if the sender dies.
/// </summary>
public sealed class ActivityStore
{
    /// <summary>Files unchanged for longer than this are treated as disconnected.</summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromMinutes(2);

    /// <summary>How long finished (done) items are shown. 6 seconds, same as the Python version.</summary>
    public static readonly TimeSpan DoneLinger = TimeSpan.FromSeconds(6);

    /// <summary>How long failed items are shown. 12 seconds, same as the Python version.</summary>
    public static readonly TimeSpan ErrorLinger = TimeSpan.FromSeconds(12);

    /// <summary>
    /// How long an agent task-done notice stays up. Kept long so it is still there when you come back from being away —
    /// 30 s meant a task finished while you were in another window was already gone when you looked (user 10-01). In normal
    /// use it clears well before this: a new prompt in that session replaces it with "working" (UserPromptSubmit hook), the
    /// ✕ dismisses it, or closing the session (SessionEnd) clears it. This is only the backstop for a done left untouched.
    /// </summary>
    public static readonly TimeSpan AgentDoneLinger = TimeSpan.FromHours(12);

    /// <summary>How long "done" is shown after a timer ends.</summary>
    public static readonly TimeSpan TimerDoneLinger = TimeSpan.FromSeconds(10);

    /// <summary>How long after its phase ended a pomodoro is still continued (a laptop closed for lunch, not overnight).</summary>
    public static readonly TimeSpan PomodoroWakeGrace = TimeSpan.FromHours(2);

    /// <summary>How long a "working" notice may stay without being replaced by done (a crashed agent's notice goes away).</summary>
    public static readonly TimeSpan WorkingLinger = TimeSpan.FromHours(6);

    /// <summary>Maximum file age worth opening (long for timers since they are written only once — one day).</summary>
    private static readonly TimeSpan TimerMaxLife = TimeSpan.FromDays(1);

    /// <summary>
    /// Whether to show this item now.
    /// Timers write their file only once at start, so they are judged by <b>end time</b>, not file age
    /// (judging by file age made a 25-minute timer vanish after 2 minutes — found on PC 09-29).
    /// Done/failed are shown briefly; everything else is considered disconnected after 2 minutes without change.
    /// </summary>
    public static bool IsAlive(ActivityState state, DateTimeOffset updated, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (state.IsPaused || TimerParser.IsStopwatch(state))
        {
            return true;   // paused timers/stopwatches stay until a human clears them (up to a file age of one day)
        }

        if (state.Due is { } due)
        {
            // A pomodoro whose phase ended while the PC slept is picked up on waking (the bar starts the next phase then): it
            // used to count as finished-and-gone, so the whole set vanished after any sleep (review 10-03). Not after hours away.
            var grace = TimerParser.PomodoroPhase(state) is null ? TimerDoneLinger : PomodoroWakeGrace;
            return now.ToUnixTimeMilliseconds() / 1000.0 <= due + grace.TotalSeconds;
        }

        var age = now - updated;
        // An agent waiting for an answer is silent too — it used to count as disconnected after 2 minutes, so coming back to the
        // desk the orange was already gone while Claude/Codex still waited (found 10-01). Answering, or closing its terminal, clears it.
        if (state.Kind is ActivityKind.AgentWorking or ActivityKind.AgentPermission)
        {
            return age <= WorkingLinger;   // no progress updates arrive while it works — don't treat silence as dead
        }

        if (state.Kind == ActivityKind.AgentDone)
        {
            return age <= AgentDoneLinger;
        }

        return state.RunState switch
        {
            ActivityRunState.Done => age <= DoneLinger,
            ActivityRunState.Error => age <= ErrorLinger,
            _ => age <= StaleAfter,   // ones stamped in the future due to clock skew (negative) are kept
        };
    }

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString,
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly List<ActivitySource> _sources;

    public ActivityStore(IEnumerable<ActivitySource> sources)
    {
        ArgumentNullException.ThrowIfNull(sources);
        _sources = [.. sources];
        if (_sources.Count == 0)
        {
            throw new ArgumentException("At least one folder to watch is required.", nameof(sources));
        }
    }

    public ActivityStore(string directory, string pattern = "*.json")
        : this([new ActivitySource(directory, pattern)])
    {
    }

    /// <summary>Folder used for new writes — the first in the list.</summary>
    public ActivitySource WriteTarget => _sources[0];

    /// <summary>Windows default locations (new folder first = where new writes go).</summary>
    public static ActivityStore CreateDefault()
        => new(DefaultSources());

    /// <summary>Folders watched by default.</summary>
    public static IReadOnlyList<ActivitySource> DefaultSources() =>
    [
        new(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "IsleBar", "state"), "*.json"),
        new(Path.Combine(Path.GetTempPath(), "tgprog"), "tgprog_*.json"),
    ];

    /// <summary>
    /// All live states, most recently changed first. Unreadable files are skipped silently
    /// (they may be mid-write; the next poll will look again).
    /// </summary>
    // Parsed states cached by path, so an unchanged file is not re-opened and re-parsed on every poll (the common case at 4×/s).
    // Only touched from Read, which runs on the one polling thread. Keyed on mtime + length so any write invalidates it.
    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _seen = new(StringComparer.OrdinalIgnoreCase);

    private readonly record struct CacheEntry(DateTime MtimeUtc, long Length, ActivityState State);

    public IReadOnlyList<ActivityState> Read(DateTimeOffset now)
    {
        var result = new List<ActivityState>();
        _seen.Clear();
        foreach (var source in _sources)
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(source.Directory, source.Pattern);
            }
            catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
            {
                continue;
            }

            foreach (var file in files)
            {
                DateTime mtimeUtc;
                long length;
                try
                {
                    var info = new FileInfo(file);   // one stat gives both mtime and length
                    mtimeUtc = info.LastWriteTimeUtc;
                    length = info.Length;
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    continue;
                }

                _seen.Add(file);
                var updated = new DateTimeOffset(mtimeUtc, TimeSpan.Zero);

                // if not a timer, filter out old ones before opening the file (most end here)
                if (now - updated > TimerMaxLife)
                {
                    continue;
                }

                ActivityState? state;
                if (_cache.TryGetValue(file, out var cached) && cached.MtimeUtc == mtimeUtc && cached.Length == length)
                {
                    state = cached.State;   // unchanged since last poll — reuse the parse, no file open
                }
                else
                {
                    state = ReadFile(file);
                    if (state is not null)
                    {
                        _cache[file] = new CacheEntry(mtimeUtc, length, state);
                    }
                }

                if (state is null || !IsAlive(state, updated, now))
                {
                    continue;
                }

                state.SourcePath = file;
                state.UpdatedAt = updated;
                result.Add(state);
            }
        }

        if (_cache.Count > _seen.Count)
        {
            foreach (var gone in _cache.Keys.Where(k => !_seen.Contains(k)).ToList())
            {
                _cache.Remove(gone);   // drop entries for files that were deleted
            }
        }

        result.Sort(static (a, b) => b.UpdatedAt.CompareTo(a.UpdatedAt));
        return result;
    }

    /// <summary>Reads one file. Null if broken or unreadable.</summary>
    public static ActivityState? ReadFile(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);   // a hook replacing the file at that moment (File.Move) must not fail — review 10-03
            return JsonSerializer.Deserialize<ActivityState>(stream, ReadOptions);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or FileNotFoundException)
        {
            return null;
        }
    }

    /// <summary>
    /// Writes a state (the path <c>islebar push</c> uses). Temp file → atomic swap, so
    /// the search bar never reads a half-written file. Returns the path written.
    /// </summary>
    /// <param name="id">Name identifying the state. Writing again with the same id overwrites the same file.</param>
    public string Write(string id, ActivityState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var path = Path.Combine(WriteTarget.Directory, FileNameFor(id));
        Directory.CreateDirectory(WriteTarget.Directory);

        var tmp = path + ".tmp-" + Guid.NewGuid().ToString("N")[..8];
        try
        {
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                JsonSerializer.Serialize(stream, state, WriteOptions);
                stream.Flush(flushToDisk: true);
            }

            // two hooks at once, or a reader without delete sharing (an older bar), can hold the target for a moment — retry briefly
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    break;
                }
                catch (Exception ex) when (attempt < 4 && ex is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(15);
                }
            }
        }
        catch
        {
            try { File.Delete(tmp); } catch (IOException) { /* ignore */ }
            throw;
        }

        return path;
    }

    /// <summary>The state written under <paramref name="id"/> (first watched folder that has it), or null.</summary>
    public ActivityState? ReadById(string id)
    {
        foreach (var source in _sources)
        {
            var path = Path.Combine(source.Directory, FileNameFor(id));
            if (File.Exists(path))
            {
                return ReadFile(path);
            }
        }

        return null;
    }

    /// <summary>Deletes a state file. False if it does not exist.</summary>
    public bool Remove(string id)
    {
        foreach (var source in _sources)
        {
            var path = Path.Combine(source.Directory, FileNameFor(id));
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                    return true;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }

        return false;
    }

    /// <summary>id to file name. Characters not allowed in paths become '_'.</summary>
    /// <summary>
    /// Deletes files of items that will no longer be shown (finished timers, expired done notices, etc.). <b>Only our folder (<see cref="WriteTarget"/>)</b> —
    /// folders other programs write to (tgprog) are left alone. Returns the number deleted.
    /// (without this, finished timer files kept piling up — 9 of them on PC 09-29)
    /// </summary>
    public int PruneDead(DateTimeOffset now)
    {
        var removed = 0;
        string[] files;
        try
        {
            files = Directory.GetFiles(WriteTarget.Directory, WriteTarget.Pattern);
        }
        catch (Exception ex) when (ex is DirectoryNotFoundException or IOException or UnauthorizedAccessException)
        {
            return 0;
        }

        foreach (var file in files)
        {
            try
            {
                var updated = new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);
                var state = ReadFile(file);
                var dead = state is null
                    ? now - updated > StaleAfter                  // broken files may be mid-write, so only old ones
                    : (!IsAlive(state, updated, now) && now - updated > TimerDoneLinger)
                      || now - updated > TimerMaxLife;            // a stopwatch or paused timer left a day: no longer shown, so not kept forever (review 10-03)
                if (dead)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    public static string FileNameFor(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);
        var safe = new string([.. id.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c)]);
        return safe.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? safe : safe + ".json";
    }
}

/// <summary>A folder to look for state files in, and the file name pattern.</summary>
/// <param name="Directory">Folder path.</param>
/// <param name="Pattern">File name pattern. E.g. <c>tgprog_*.json</c>.</param>
public sealed record ActivitySource(string Directory, string Pattern = "*.json");
