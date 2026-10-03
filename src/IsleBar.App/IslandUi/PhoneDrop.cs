using IsleBar.Core.Island;
using IsleBar.App.Interop;

namespace IsleBar.App.IslandUi;

/// <summary>
/// Passes files dropped onto the pill to the user's "drop command" from settings (e.g. a script that sends them to a phone).
/// With no command set, dropping attaches the files to the prompt instead (MainWindow) and this class isn't used.
/// While sending, the island shows "📤 PC → Phone · file name" (unknown progress = flowing bar); when done, "sent"/"failed" for 3 s.
/// If the command reports its own progress for the same file name (a transfer state file), our display steps aside so it isn't shown twice.
/// </summary>
internal sealed class PhoneDrop
{
    private static readonly TimeSpan ShowDone = TimeSpan.FromSeconds(3);
    private readonly object _gate = new();
    private readonly List<(ActivityState State, DateTimeOffset? EndedAt)> _sends = [];

    /// <summary>Sends to show on the island. Safe to read from any thread.</summary>
    public IReadOnlyList<ActivityState> Current(IEnumerable<ActivityState> others)
    {
        var now = DateTimeOffset.UtcNow;
        var otherNames = others.Where(o => o.Kind == ActivityKind.Transfer).Select(o => o.Name).ToHashSet();
        lock (_gate)
        {
            _sends.RemoveAll(s => s.EndedAt is { } end && now - end > ShowDone);
            return _sends.Select(s => s.State).Where(s => !otherNames.Contains(s.Name)).ToList();
        }
    }

    /// <summary>Folders bigger than this (before compression) aren't zipped — dropping a whole drive by mistake would fill the disk.</summary>
    private const long MaxFolderBytes = 4L * 1024 * 1024 * 1024;

    /// <summary>Where folder zips are made — one subfolder per send; old ones are swept on the next send.</summary>
    private static string DropRoot => Path.Combine(Path.GetTempPath(), "IsleBar", "drop");

    /// <summary>
    /// Sends the files. Folders are zipped first and the zip is sent in their place (user 10-01: a folder used to be skipped
    /// silently while the pill still showed "drop here"). The zips stay in %TEMP%\IsleBar\drop for an hour — a command may hand
    /// the file to another program and exit before that program has read it — and are swept on a later send.
    /// False if there is no send command or nothing to send.
    /// </summary>
    public bool Send(IReadOnlyList<string> paths, string? configuredCommand)
    {
        var items = paths.Where(p => File.Exists(p) || Directory.Exists(p)).ToList();
        if (items.Count == 0 || ResolveCommand(configuredCommand) is not { } command)
        {
            return false;
        }

        var first = Directory.Exists(items[0]) ? ZipName(items[0]) : Path.GetFileName(items[0]);
        var state = new ActivityState
        {
            RawKind = ActivityState.KindTransfer,
            Title = "📤 PC → Phone",
            Name = first + (items.Count > 1 ? $" +{items.Count - 1}" : string.Empty),
            State = "run",
            T0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            SourcePath = "drop:" + Guid.NewGuid().ToString("N"),
        };

        lock (_gate)
        {
            _sends.Add((state, null));
        }

        // Zipping can take a while — off the UI thread; the island already shows the send as running.
        _ = Task.Run(() =>
        {
            try
            {
                SweepOldZips();
                var sendDir = Path.Combine(DropRoot, Guid.NewGuid().ToString("N")[..8]);
                var files = new List<string>();
                for (var i = 0; i < items.Count; i++)
                {
                    // each folder gets its own subfolder, so a\src and b\src don't collide as src.zip (code review 10-01)
                    files.Add(File.Exists(items[i]) ? items[i] : ZipFolder(items[i], Path.Combine(sendDir, i.ToString(System.Globalization.CultureInfo.InvariantCulture))));
                }

                Run(command, files, state);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // anything at all — otherwise the pill would say "sending" forever (code review 10-01)
                TaskbarHost.Log("drop→command failed: " + ex.Message);
                Finish(state, ok: false);
            }
        });
        return true;
    }

    /// <summary>"name.zip" for a folder; a whole drive becomes "Drive_C.zip" rather than ".zip".</summary>
    private static string ZipName(string folder)
    {
        var trimmed = folder.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(name))
        {
            name = "Drive_" + new string(trimmed.Where(char.IsLetterOrDigit).ToArray());
        }

        return name + ".zip";
    }

    /// <summary>Zips a folder (the folder itself becomes the zip's top entry) into <paramref name="dir"/>.</summary>
    private static string ZipFolder(string folder, string dir)
    {
        var size = new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length);
        if (size > MaxFolderBytes)
        {
            throw new IOException($"folder too big to zip ({size / (1024 * 1024)} MB): {folder}");
        }

        Directory.CreateDirectory(dir);
        var zip = Path.Combine(dir, ZipName(folder));
        System.IO.Compression.ZipFile.CreateFromDirectory(folder, zip, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: true);
        return zip;
    }

    private void Run((string Exe, IReadOnlyList<string> Args) command, List<string> files, ActivityState state)
    {
        // A .bat / .cmd runs through cmd.exe, which re-reads the file names: "&" in a name ran another command, %NAME% was
        // replaced (review 10-03). Such names are refused for a batch script (an .exe or a Python script gets them intact).
        if ((command.Exe.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) || command.Exe.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
            && files.FirstOrDefault(f => f.IndexOfAny(['&', '|', '<', '>', '^', '%', '!', '"']) >= 0) is { } risky)
        {
            throw new InvalidOperationException($"a batch-script drop command can't take this file name safely: {Path.GetFileName(risky)}");
        }

        var start = new System.Diagnostics.ProcessStartInfo(command.Exe) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var arg in command.Args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var file in files)
        {
            start.ArgumentList.Add(file);
        }

        var process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException("the drop command didn't start");
        // subscribe before raising is switched on — a command that exits at once would otherwise leave the send "running" forever
        process.Exited += (_, _) =>
        {
            Finish(state, process.ExitCode == 0);
            process.Dispose();
        };
        process.EnableRaisingEvents = true;
    }

    /// <summary>Removes send folders older than an hour (left by earlier sends or a crash). Never throws.</summary>
    private static void SweepOldZips()
    {
        try
        {
            if (!Directory.Exists(DropRoot))
            {
                return;
            }

            foreach (var dir in Directory.GetDirectories(DropRoot))
            {
                if (DateTime.UtcNow - Directory.GetCreationTimeUtc(dir) > TimeSpan.FromHours(1))
                {
                    DeleteQuietly(dir);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover temp zip is harmless
        }
    }

    private void Finish(ActivityState state, bool ok)
    {
        lock (_gate)
        {
            var i = _sends.FindIndex(s => ReferenceEquals(s.State, state));
            if (i >= 0)
            {
                state.State = ok ? "done" : "error";
                _sends[i] = (state, DateTimeOffset.UtcNow);
            }
        }
    }

    private static void DeleteQuietly(string? dir)
    {
        try
        {
            if (dir is not null && Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a leftover temp zip is harmless
        }
    }

    /// <summary>
    /// Send command (executable + leading arguments). The setting has the form <c>"executable" args...</c> (file paths are appended).
    /// No setting, no command — there is no built-in default.
    /// </summary>
    private static (string Exe, IReadOnlyList<string> Args)? ResolveCommand(string? configured)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            return null;
        }

        var parts = SplitCommandLine(configured);
        return parts.Count > 0 ? (parts[0], parts.Skip(1).ToList()) : null;
    }

    private static List<string> SplitCommandLine(string line)
    {
        var parts = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;
        foreach (var c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
            }
            else
            {
                current.Append(c);
            }
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }
}
