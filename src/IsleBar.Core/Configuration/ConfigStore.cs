using System.Text.Json;
using System.Text.Json.Nodes;

namespace IsleBar.Core.Configuration;

/// <summary>
/// Reads/writes the settings file. A port of the Python version's <c>_read_cfg</c>/<c>_write_cfg</c>
/// with cross-process locking added (the search bar, islebar push and the supervisor all use it).
///
/// Rules
///  - Broken JSON, or JSON that is not an object → all defaults. Before the first save over it, the broken file is kept as
///    <c>islebar.json.broken-yyyyMMdd-HHmmss</c> — otherwise that save replaced every setting and the history with defaults for good
///    (found in a corrupted-file test 10-01).
///  - If one key has the wrong type, only that key falls back to default; the rest are kept.
///  - Keys we do not know are preserved as-is.
///  - Saving is lock → temp file → atomic swap. No half-written file is ever left behind.
/// </summary>
public sealed class ConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions ReadOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    private readonly object _gate = new();

    public ConfigStore(string path)
    {
        Path = path ?? throw new ArgumentNullException(nameof(path));
        LockPath = path + ".lock";
    }

    public string Path { get; }

    private string LockPath { get; }

    /// <summary>How long to wait for the lock. We do not get stuck here even if another process dies.</summary>
    public TimeSpan LockTimeout { get; init; } = TimeSpan.FromSeconds(3);

    /// <summary>Settings read from the file and shape-checked. Defaults on failure.</summary>
    public IsleBarSettings Load() => SettingsCodec.FromJson(LoadRaw());

    /// <summary>The settings, or null when the file exists but can't be read right now (locked, no access).</summary>
    public IsleBarSettings? TryLoad()
    {
        lock (_gate)
        {
            var raw = ReadRawNoLock(out _, out var unreadable);
            return unreadable ? null : SettingsCodec.FromJson(raw);
        }
    }

    /// <summary>The file's raw JSON object. Empty object if missing or broken.</summary>
    public JsonObject LoadRaw()
    {
        lock (_gate)
        {
            return ReadRawNoLock(out _, out _);
        }
    }

    private JsonObject ReadRawNoLock(out bool broken, out bool unreadable)
    {
        broken = false;
        unreadable = false;
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                var node = JsonNode.Parse(stream, documentOptions: ReadOptions);
                broken = node is not JsonObject;
                if (node is JsonObject obj)
                {
                    _ = obj.Count;   // a repeated key only throws when the object is first used — find out here (review 10-03)
                    return obj;
                }

                return [];
            }
            catch (FileNotFoundException) { return []; }
            catch (DirectoryNotFoundException) { return []; }
            catch (JsonException) { broken = true; return []; }          // broken JSON → defaults
            catch (ArgumentException) { broken = true; return []; }      // a key written twice (hand edit) — crashed at start before
            catch (IOException) when (attempt < 10)        // collided with the swap at that instant, or briefly locked (up to ~0.5 s)
            {
                Thread.Sleep(attempt < 4 ? 20 : 80);
            }
            catch (IOException) { unreadable = true; return []; }
            catch (UnauthorizedAccessException) { unreadable = true; return []; }
        }
    }

    /// <summary>Saves the settings (unknown keys preserved). True on success.</summary>
    public bool Save(IsleBarSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return Update(current =>
        {
            SettingsCodec.Apply(settings, current);
            return true;
        });
    }

    /// <summary>
    /// Does read → modify → write within a single lock. Even when called from several places at once
    /// they do not overwrite each other's changes. If <paramref name="mutate"/> returns false, nothing is written and true is returned.
    /// </summary>
    public bool Update(Func<JsonObject, bool> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        lock (_gate)
        {
            using var fileLock = FileLock.Acquire(LockPath, LockTimeout);
            var raw = ReadRawNoLock(out var broken, out var unreadable);
            if (unreadable)
            {
                return false;   // couldn't read it: writing now would replace every setting with the defaults (review 10-03)
            }

            if (!mutate(raw))
            {
                return true;
            }

            if (broken)
            {
                KeepBrokenCopy();
            }

            return WriteAtomic(raw);
        }
    }

    /// <summary>Strongly typed version of <see cref="Update(Func{JsonObject,bool})"/>.</summary>
    public bool Update(Func<IsleBarSettings, bool> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);
        return Update(raw =>
        {
            var settings = SettingsCodec.FromJson(raw);
            if (!mutate(settings))
            {
                return false;
            }

            SettingsCodec.Apply(settings, raw);
            return true;
        });
    }

    /// <summary>Copies an unreadable settings file aside before it's overwritten, so a hand edit gone wrong can be recovered.</summary>
    private void KeepBrokenCopy()
    {
        try
        {
            File.Copy(Path, Path + ".broken-" + DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // best effort — never block saving on it
        }
    }

    private bool WriteAtomic(JsonObject raw)
    {
        var dir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(Path))!;
        var tmp = System.IO.Path.Combine(dir, System.IO.Path.GetFileName(Path) + ".tmp-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(dir);
            var text = raw.ToJsonString(WriteOptions);
            using (var stream = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(stream, new System.Text.UTF8Encoding(false)))
            {
                writer.Write(text);
                writer.Flush();
                stream.Flush(flushToDisk: true);   // so no half-written file is left even if power is lost
            }

            File.Move(tmp, Path, overwrite: true); // atomic swap
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            try { File.Delete(tmp); } catch (IOException) { /* fine if it cannot be deleted */ }
            catch (UnauthorizedAccessException) { /* ignore */ }
            return false;
        }
    }
}
