using IsleBar.App.Supervisor;
using Microsoft.UI.Xaml;

namespace IsleBar.App;

/// <summary>
/// Writes the entry point by hand — unlike the one WinUI generates, it <b>must be able to return an exit code</b>
/// so the supervising launcher knows "why it ended" (0 user quit · 3 error · 4 restart requested · 5 already running).
///
/// The csproj's <c>DISABLE_XAML_GENERATED_MAIN</c> turns off the XAML-generated Main (confirmed in PC build 09-29).
/// </summary>
public static partial class Program
{
    [System.Runtime.InteropServices.LibraryImport("kernel32.dll")]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static partial bool FreeConsole();

    /// <summary>Marker the supervising launcher adds when starting the search box (child).</summary>
    private const string ChildFlag = "--child";

    [STAThread]
    public static int Main(string[] args)
    {
        // Launched via dotnet.exe (a console program) during development, we inherit a hidden console. Left as-is, claude
        // would also start inside that hidden console and its window would be invisible (measured on PC 09-29) → detach so a new terminal window opens.
        FreeConsole();

        // Started plainly, this becomes the supervising launcher: it starts the search box (child) and restarts it on theme change,
        // Explorer restart or unexpected death. It exits too when the user quits (0). (Same role as the Python version's claude_bar_run.pyw — 09-30)
        if (!args.Contains(ChildFlag))
        {
            return Supervise();
        }

        Microsoft.UI.Xaml.Application.Start(_p =>
        {
            var context = new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(
                Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            System.Threading.SynchronizationContext.SetSynchronizationContext(context);
            _ = new App();
        });

        return App.ExitCode;
    }

    private static int Supervise()
    {
        var mutex = new Mutex(initiallyOwned: true, @"Local\islebar_supervisor_ds3owl", out var mine);
        if (!mine)
        {
            return ExitCodes.AlreadyRunning;   // a supervising launcher already exists
        }

        try
        {
            Interop.CrashReports.StartIfAllowed();
            var backoff = new IsleBar.Core.Supervisor.RelaunchBackoff();
            while (true)
            {
                var started = DateTime.UtcNow;
                int code;
                try
                {
                    using var child = System.Diagnostics.Process.Start(ChildStartInfo())!;
                    child.WaitForExit();
                    code = child.ExitCode;
                }
                catch (System.ComponentModel.Win32Exception)
                {
                    code = ExitCodes.Abnormal;
                }

                if (code == ExitCodes.AlreadyRunning && AdoptRunningBar() is { } adopted)
                {
                    // A bar left without its supervisor (the supervisor was ended in Task Manager, an installer…) still runs: the
                    // new child found it and quit. Watch that one instead of giving up — it used to stay unsupervised, so its next
                    // restart request (theme, DPI, Explorer) ended it for good (review 10-03).
                    started = DateTime.UtcNow;
                    code = adopted;
                }

                Interop.TaskbarHost.Log($"supervisor: bar exited with code {code} (ran {(DateTime.UtcNow - started).TotalSeconds:0}s)");
                if (IsleBar.Core.Diagnostics.CrashText.IsNativeCrash(code))
                {
                    Interop.CrashReports.StartIfAllowed();   // the person may have opted in since the supervisor started
                    Interop.CrashReports.BarExited(code, DateTime.UtcNow - started);
                }
                if (!ExitCodes.ShouldRelaunch(code))
                {
                    return code;
                }

                Thread.Sleep(backoff.After(code, DateTime.UtcNow - started));
            }
        }
        finally
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    /// <summary>
    /// Waits for a bar process this supervisor didn't start (one left behind by an earlier supervisor) and returns its exit code;
    /// null when there is none. Same executable, another process id — the supervisor mutex guarantees no other supervisor lives.
    /// </summary>
    private static int? AdoptRunningBar()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        if (self.ProcessName.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            return null;   // a development run under dotnet.exe: other dotnet processes aren't bars
        }

        foreach (var other in System.Diagnostics.Process.GetProcessesByName(self.ProcessName))
        {
            using (other)
            {
                try
                {
                    if (other.Id == self.Id || other.HasExited
                        || !string.Equals(other.MainModule?.FileName, Environment.ProcessPath, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;   // only this install's bar — not a Store / dev build of the same name
                    }

                    Interop.TaskbarHost.Log($"supervisor: watching the bar that was already running (pid {other.Id})");
                    other.WaitForExit();
                    return other.ExitCode;
                }
                catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                {
                    // gone meanwhile, or not ours to watch
                }
            }
        }

        return null;
    }

    /// <summary>Relaunches ourselves as the child. If running under dotnet.exe (development), passes the DLL path.</summary>
    private static System.Diagnostics.ProcessStartInfo ChildStartInfo()
    {
        var host = Environment.ProcessPath!;
        var info = new System.Diagnostics.ProcessStartInfo(host)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        if (Path.GetFileNameWithoutExtension(host).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            info.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "IsleBar.App.dll"));
        }

        info.ArgumentList.Add(ChildFlag);
        return info;
    }
}
