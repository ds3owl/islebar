using System.Runtime.InteropServices;
using IsleBar.App.Interop;
using IsleBar.App.Supervisor;
using Microsoft.UI.Xaml;
using static IsleBar.App.Interop.NativeMethods;

namespace IsleBar.App;

/// <summary>
/// App startup. Order matters:
///  1. DPI mode PerMonitorV2 (check whether the manifest already set it) — <b>otherwise it can't attach to the taskbar</b>
///  2. Set a distinct app ID so the taskbar icon isn't grouped with other apps
///  3. If already running, exit quietly (exit code 5)
///
/// <b>This file has never been built. Needs phase-0 verification on a PC.</b>
/// </summary>
public partial class App : Application
{
    private IntPtr _mutex;
    private MainWindow? _window;

    public App()
    {
        DpiSetup.Ensure();
        SetCurrentProcessExplicitAppUserModelID(AppPaths.AppUserModelId);
        InitializeComponent();

        // Record why the process died before it goes. The watchdog/launcher brings the bar back, but until now nothing said what
        // crashed it (10-01). We log and let it fall through — a clean restart beats limping on in an unknown state.
        CrashReports.StartIfAllowed();   // opt-in only (setup checkbox or settings) — 10-01
        UnhandledException += (_, e) =>
        {
            AppLog.Write($"FATAL (ui) {e.Message}{Environment.NewLine}{e.Exception}");
            CrashReports.Capture(e.Exception, "ui");
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            AppLog.Write($"FATAL (background thread) isTerminating={e.IsTerminating}{Environment.NewLine}{e.ExceptionObject}");
            CrashReports.Capture(e.ExceptionObject as Exception, "background");
        };
        System.Threading.Tasks.TaskScheduler.UnobservedTaskException += (_, e) =>
        {
            AppLog.Write($"task exception (unobserved){Environment.NewLine}{e.Exception}");
            e.SetObserved();   // don't let a stray background task tear the whole app down
        };
    }

    /// <summary>Exit code reported to the supervising launcher.</summary>
    internal static int ExitCode { get; set; } = ExitCodes.UserQuit;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _mutex = CreateMutex(IntPtr.Zero, false, AppPaths.SingleInstanceMutex);
        if (Marshal.GetLastWin32Error() == ERROR_ALREADY_EXISTS)
        {
            ExitCode = ExitCodes.AlreadyRunning;
            Exit();
            return;
        }

        _window = new MainWindow();
        _window.Closed += (_, _) =>
        {
            if (_mutex != IntPtr.Zero)
            {
                CloseHandle(_mutex);
                _mutex = IntPtr.Zero;
            }
        };
        _window.Activate();
    }
}
