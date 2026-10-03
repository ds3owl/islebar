namespace IsleBar.Core.Configuration;

/// <summary>
/// Cross-process lock. Grabs a lock file with exclusive open; if it cannot within the timeout,
/// <b>it just proceeds</b> — being unable to write settings because the lock holder died is worse.
/// (Thanks to the atomic swap the file does not get corrupted even without the lock. The lock is there
/// to keep read-modify-write cycles from overlapping.)
/// </summary>
internal sealed class FileLock : IDisposable
{
    private readonly FileStream? _stream;

    private FileLock(FileStream? stream) => _stream = stream;

    /// <summary>Whether the lock was acquired. False means the timeout passed and we yielded and proceeded anyway.</summary>
    public bool Held => _stream is not null;

    public static FileLock Acquire(string path, TimeSpan timeout)
    {
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        var delayMs = 5;
        while (true)
        {
            try
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var stream = new FileStream(
                    path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None,
                    bufferSize: 1, FileOptions.DeleteOnClose);
                return new FileLock(stream);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                if (Environment.TickCount64 >= deadline)
                {
                    return new FileLock(null);
                }

                Thread.Sleep(delayMs);
                delayMs = Math.Min(delayMs * 2, 50);
            }
        }
    }

    public void Dispose()
    {
        try { _stream?.Dispose(); }
        catch (IOException) { /* ignore DeleteOnClose failure */ }
    }
}
