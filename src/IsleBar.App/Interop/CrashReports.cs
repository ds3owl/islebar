using IsleBar.Core.Diagnostics;
using Microsoft.Win32;
using Sentry;

namespace IsleBar.App.Interop;

/// <summary>
/// Opt-in crash reports (user 10-01: most people never file bug reports). Off unless the person ticked it in setup or in
/// settings — the choice lives in <c>HKCU\Software\IsleBar\CrashReports</c>, shared by the installer, the settings window, the
/// supervisor and the bar.
///
/// What is sent: the error type, where in IsleBar's code it happened, the app version, Windows version and UI language.
/// What never is: window titles, file names or paths, prompts, logs, the computer or user name, the IP address (the Sentry
/// project also refuses to store IPs and scrubs data server-side). Reports go to Sentry's EU region.
/// </summary>
internal static class CrashReports
{
    private const string Dsn = "https://196d1f039c95602787eeb8fd27191849@o4512180519632896.ingest.de.sentry.io/4512180736360528";
    private const string KeyPath = @"Software\IsleBar";
    private const string ValueName = "CrashReports";

    private static bool _started;

    /// <summary>The person's choice. Reading never throws.</summary>
    public static bool Consent
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
                return key?.GetValue(ValueName) is int v && v == 1;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return false;
            }
        }
        set
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(KeyPath);
                key.SetValue(ValueName, value ? 1 : 0, RegistryValueKind.DWord);
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
            {
                return;
            }

            if (value)
            {
                Start();
            }
            else
            {
                Stop();
            }
        }
    }

    /// <summary>Starts reporting if the person agreed. Safe to call more than once.</summary>
    public static void StartIfAllowed()
    {
        if (Consent)
        {
            Start();
        }
    }

    private static void Start()
    {
        if (_started)
        {
            return;
        }

        try
        {
            SentrySdk.Init(options =>
            {
                options.Dsn = Dsn;
                options.Release = "islebar@" + (typeof(CrashReports).Assembly.GetName().Version?.ToString(3) ?? "0");
                options.Environment = "release";
                options.IsGlobalModeEnabled = true;          // a desktop app: one user, one scope
                options.SendDefaultPii = false;
                options.ServerName = string.Empty;           // the default is the computer's name
                options.MaxBreadcrumbs = 0;                  // breadcrumbs could carry what was on screen
                options.AutoSessionTracking = false;         // no usage tracking — crashes only
                options.AttachStacktrace = true;
                options.CacheDirectoryPath = Path.Combine(AppPaths.DataDirectory, "crash-reports");   // a hard crash is sent on the next start
                options.SetBeforeSend((e, _) => Scrub(e));
                options.DisableAppDomainUnhandledExceptionCapture();   // our own handlers capture (and flush) those — no duplicates
            });
            _started = true;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write("crash reports: could not start — " + ex.Message);
        }
    }

    private static void Stop()
    {
        if (_started)
        {
            SentrySdk.Close();
            _started = false;
        }

        // reports still waiting to be sent (from a crash before the opt-out) must not go out if the person opts in again later
        try
        {
            var cache = Path.Combine(AppPaths.DataDirectory, "crash-reports");
            if (Directory.Exists(cache))
            {
                Directory.Delete(cache, recursive: true);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("crash reports: could not clear the queue — " + ex.Message);
        }
    }

    /// <summary>
    /// The supervisor and the bar are separate processes and only the bar has the settings window, so a process that started
    /// reporting earlier re-reads the choice before each report and stops if it was turned off since (code review 10-02).
    /// </summary>
    private static bool StillAllowed()
    {
        if (!_started)
        {
            return false;
        }

        if (Consent)
        {
            return true;
        }

        Stop();
        return false;
    }

    /// <summary>Records an exception that is about to take the bar down, and waits briefly so it leaves before we do.</summary>
    public static void Capture(Exception? ex, string where)
    {
        if (ex is null || !StillAllowed())
        {
            return;
        }

        SentrySdk.CaptureException(ex, scope => scope.SetTag("where", where));
        SentrySdk.Flush(TimeSpan.FromSeconds(2));
        if (where != "ui")
        {
            return;   // only a UI exception ends in WinUI's fail-fast; a background one exits otherwise and no mark would be consumed
        }

        try
        {
            File.WriteAllText(ReportedMarker, where);   // the supervisor then knows this death is already reported
        }
        catch (Exception markEx) when (markEx is IOException or UnauthorizedAccessException)
        {
            AppLog.Write("crash reports: could not leave the reported mark — " + markEx.Message);
        }
    }

    /// <summary>
    /// Left by the bar when it has just reported its own crash. A UI exception is followed by WinUI's fail-fast (0xC000027B), so the
    /// supervisor saw a native crash and sent a second report of the same death (found 10-02, fixed 10-03).
    /// </summary>
    private static string ReportedMarker => Path.Combine(AppPaths.DataDirectory, "crash-reported.mark");

    /// <summary>True if the bar reported this crash itself in the last minute. Clears the mark either way.</summary>
    private static bool BarAlreadyReported()
    {
        try
        {
            var mark = new FileInfo(ReportedMarker);
            if (!mark.Exists)
            {
                return false;
            }

            var recent = DateTime.UtcNow - mark.LastWriteTimeUtc < TimeSpan.FromMinutes(1);
            mark.Delete();
            return recent;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// The supervisor saw the bar die without a managed exception (a native fail-fast such as 0xC000027B, heap corruption,
    /// …) — the exit code is all there is, and all that is sent.
    /// </summary>
    public static void BarExited(int exitCode, TimeSpan ranFor)
    {
        if (BarAlreadyReported())
        {
            AppLog.Write("crash reports: the bar already reported this exit — not sent twice");
            return;
        }

        if (!StillAllowed())
        {
            return;
        }

        var hex = "0x" + unchecked((uint)exitCode).ToString("X8", System.Globalization.CultureInfo.InvariantCulture);
        SentrySdk.CaptureMessage($"bar exited abnormally ({hex})", scope =>
        {
            scope.Level = SentryLevel.Error;
            scope.SetTag("exit_code", hex);
            scope.SetTag("ran_for", ranFor < TimeSpan.FromMinutes(1) ? "<1m" : ranFor < TimeSpan.FromHours(1) ? "<1h" : ">=1h");
            scope.SetFingerprint(["bar-exit", hex]);
        });
        SentrySdk.Flush(TimeSpan.FromSeconds(2));
    }

    /// <summary>Last line of defence before anything leaves the PC.</summary>
    internal static SentryEvent Scrub(SentryEvent e)
    {
        e.ServerName = null;
        // an explicit blank address: otherwise Sentry looks the sender's IP up and files a city with the report (seen 10-01),
        // even with IP storage turned off for the project
        e.User = new SentryUser { IpAddress = "0.0.0.0" };
        e.Contexts.Device.Name = null;
        e.Contexts.Device.Timezone = null;   // coarse location
        e.Contexts.Device.BootTime = null;
        e.Message = e.Message is null ? null : new SentryMessage { Formatted = Clean(e.Message.Formatted ?? e.Message.Message) };
        if (e.SentryExceptions is { } exceptions)
        {
            foreach (var x in exceptions)
            {
                x.Value = Clean(x.Value);
                foreach (var frame in x.Stacktrace?.Frames ?? [])
                {
                    frame.AbsolutePath = null;   // where the file was on the build machine — not needed to read the stack
                    frame.FileName = FileOnly(frame.FileName);
                    frame.Vars.Clear();
                }
            }
        }

        // loaded modules are listed with their full path, which holds the Windows user name (C:\Users\<name>\AppData\...)
        foreach (var image in e.DebugImages ?? [])
        {
            image.CodeFile = FileOnly(image.CodeFile);
            image.DebugFile = FileOnly(image.DebugFile);
        }

        return e;
    }

    private static string? FileOnly(string? path) => string.IsNullOrEmpty(path) ? path : Path.GetFileName(path.Replace('/', '\\'));

    private static string? Clean(string? text) => CrashText.Clean(text);
}
