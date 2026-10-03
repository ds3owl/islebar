using System.Text;

namespace IsleBar.App.Interop;

/// <summary>
/// The app's one log file (<c>%LOCALAPPDATA%\IsleBar\logs\islebar.log</c>). Appends a timestamped line and <b>rotates</b> when
/// the file grows past a cap, so a process that runs 24/7 can never quietly fill the disk — the old content moves to
/// <c>islebar.log.1</c> (one backup kept) and a fresh file starts. Thread-safe: the watchdog thread, the UI thread and the
/// taskbar host all log through here (added 10-01; before this each writer appended with no size limit).
/// </summary>
internal static class AppLog
{
    /// <summary>Rotate once the current file passes this size. One backup is kept, so the log costs at most ~2× this on disk.</summary>
    private const long MaxBytes = 1 << 20;   // 1 MB

    private static readonly object Gate = new();

    private static string LogFile => Path.Combine(AppPaths.LogDirectory, "islebar.log");

    /// <summary>Appends one timestamped line. Never throws — failing to log must not disturb the app.</summary>
    public static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                var path = LogFile;
                RotateIfBig(path);
                File.AppendAllText(path, $"{DateTime.Now:MM-dd HH:mm:ss} {message}{Environment.NewLine}", Encoding.UTF8);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // failing to log is fine
            }
        }
    }

    private static void RotateIfBig(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length < MaxBytes)
            {
                return;
            }

            var backup = path + ".1";
            if (File.Exists(backup))
            {
                File.Delete(backup);
            }

            File.Move(path, backup);   // the current file becomes the single backup; the next append starts a fresh one
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // if rotation fails, keep appending to the existing file rather than lose the message
        }
    }
}
