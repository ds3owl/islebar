using IsleBar.Core.Island;

namespace IsleBar.App.Media;

/// <summary>
/// Puts browser downloads on the island (decision card #3: "download progress — extend to browser downloads").
/// While downloading, browsers append <c>.crdownload</c> (Chrome, Edge, Whale, etc.) or <c>.part</c> (Firefox) to the file,
/// so we check the downloads folder every second: downloading → bytes received + flowing bar (total size can't be known from the file);
/// when the partial file disappears and the original-name file appears → done (clicking shows that file in Explorer).
/// Nothing is written to disk; memory only. If the size stays the same for over 2 minutes (paused), it's dropped.
/// </summary>
internal sealed class DownloadWatcher : IDisposable
{
    private static readonly string[] PartialExtensions = [".crdownload", ".part", ".opdownload", ".download"];
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Stalled = TimeSpan.FromMinutes(2);

    private readonly string _folder;
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, DateTimeOffset> _firstSeen = [];
    private readonly List<ActivityState> _finished = [];
    private HashSet<string> _lastPartials = [];

    public DownloadWatcher(string? folder = null)
    {
        _folder = folder ?? DefaultFolder();
    }

    /// <summary>Downloads to show now (in progress + just finished). Safe to read from any thread.</summary>
    public IReadOnlyList<ActivityState> Current { get; private set; } = [];

    public void Start() => _ = Task.Run(LoopAsync);

    /// <summary>The user's downloads folder (if relocated, that location — the path the shell knows).</summary>
    private static string DefaultFolder()
    {
        try
        {
            var path = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Explorer\User Shell Folders",
                "{374DE290-123F-4565-9164-39C4925E467B}", null) as string;
            if (!string.IsNullOrWhiteSpace(path))
            {
                return Environment.ExpandEnvironmentVariables(path);
            }
        }
        catch (System.Security.SecurityException)
        {
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }

    private async Task LoopAsync()
    {
        while (!_stop.IsCancellationRequested)
        {
            try
            {
                Current = Scan(DateTimeOffset.UtcNow);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Current = [];
            }

            try
            {
                await Task.Delay(PollInterval, _stop.Token);
            }
            catch (TaskCanceledException)
            {
                return;
            }
        }
    }

    private List<ActivityState> Scan(DateTimeOffset now)
    {
        var result = new List<ActivityState>();
        var partials = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (Directory.Exists(_folder))
        {
            foreach (var file in Directory.EnumerateFiles(_folder))
            {
                if (!PartialExtensions.Any(ext => file.EndsWith(ext, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                var info = new FileInfo(file);
                if (now - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) > Stalled)
                {
                    continue;   // paused or abandoned partial file
                }

                partials.Add(file);
                if (!_firstSeen.ContainsKey(file))
                {
                    _firstSeen[file] = now;
                }

                result.Add(new ActivityState
                {
                    RawKind = ActivityState.KindTransfer,
                    Title = "Download → PC",            // ending with "PC" means receiving (↓)
                    Name = FinalName(file),
                    Stage = "download",
                    Done = info.Length,                 // bytes received (total size unknown)
                    State = "run",
                    SourcePath = "download:" + file,
                    UpdatedAt = _firstSeen[file],
                });
            }
        }

        // The partial file from last time is gone and the original-name file exists → done (shown briefly)
        foreach (var gone in _lastPartials.Where(p => !partials.Contains(p)))
        {
            var final = Path.Combine(_folder, FinalName(gone));
            _firstSeen.Remove(gone);
            // really finished: the partial file is gone (not just stalled) and the file has content — Firefox keeps an empty
            // placeholder under the final name while it downloads, and a paused download read as "done" (review 10-03)
            if (!File.Exists(gone) && File.Exists(final) && new FileInfo(final).Length > 0)
            {
                _finished.Add(new ActivityState
                {
                    RawKind = ActivityState.KindTransfer,
                    Title = "Download → PC",
                    Name = Path.GetFileName(final),
                    State = "done",
                    Open = final,
                    SourcePath = "download-done:" + final,
                    UpdatedAt = now,
                });
            }
        }

        _lastPartials = partials;
        _finished.RemoveAll(f => now - f.UpdatedAt > ActivityStore.DoneLinger);
        result.AddRange(_finished);
        return result;
    }

    /// <summary>"report.pdf.crdownload" → "report.pdf". Chrome's "Unconfirmed 123.crdownload" stays as-is (name not known yet).</summary>
    private static string FinalName(string partial)
    {
        var name = Path.GetFileName(partial);
        foreach (var ext in PartialExtensions)
        {
            if (name.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            {
                return name[..^ext.Length];
            }
        }

        return name;
    }

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }
}
