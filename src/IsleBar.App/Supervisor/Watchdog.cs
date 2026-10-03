using IsleBar.App.Interop;
using IsleBar.Core.Supervisor;

namespace IsleBar.App.Supervisor;

/// <summary>
/// Watches itself and restarts when something is wrong. Port of the Python version's <c>_guard</c>.
///
/// Cases that require a restart:
///  - the window disappeared
///  - Explorer restarted and the taskbar window (Shell_TrayWnd) changed
///  - the theme changed → colors must be re-sampled. With <c>onThemeChanged</c> the bar recolours itself in place instead
///    (a restart made the pill vanish and the real search box flash for a second — found filming the theme switch 10-01)
///
/// <b>The decision rules themselves live in <see cref="HealthCheck"/> (Core)</b> — moved there to test without a window (09-29).
/// What remains here is only asking once per second and acting on the result.
///
/// <b>This file has never been built. Needs phase-0 verification on PC.</b>
/// </summary>
internal sealed class Watchdog(TaskbarHost host, Func<bool> isLightTheme, Action<int> requestExit, Action<bool>? onThemeChanged = null) : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly CancellationTokenSource _stop = new();
    private readonly bool _startedLight = isLightTheme();
    private Thread? _thread;

    /// <summary>Stops monitoring while the user is quitting (so quitting isn't treated as a fault).</summary>
    public bool Quitting { get; set; }

    public void Start()
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "IsleBar watchdog" };
        _thread.Start();
    }

    private void Loop()
    {
        var attachedTo = host.Host;
        var startedDpi = TaskbarDpi();
        var startedLight = _startedLight;
        while (!_stop.Token.WaitHandle.WaitOne(Interval))
        {
            if (Quitting)
            {
                return;
            }

            if (startedDpi == 0)
            {
                startedDpi = TaskbarDpi();   // no taskbar yet when we started — take the first real reading as the baseline
            }

            var verdict = HealthCheck.Check(
                windowAlive: host.WindowAlive,
                attachedTaskbar: attachedTo,
                currentTaskbar: TaskbarHost.FindTaskbar(),
                startedLight: startedLight,
                nowLight: isLightTheme(),
                startedDpi: startedDpi,
                nowDpi: TaskbarDpi());

            if (verdict.Healthy)
            {
                continue;
            }

            if (verdict.Problem == HealthProblem.ThemeChanged && onThemeChanged is not null)
            {
                startedLight = !startedLight;
                Log("theme changed — recolouring in place");
                onThemeChanged(startedLight);
                continue;
            }

            Log($"restart: {verdict.Reason}");
            if (verdict.SettleDelay > TimeSpan.Zero)
            {
                Thread.Sleep(verdict.SettleDelay);
            }

            requestExit(verdict.ExitCode);
            return;
        }
    }

    private static void Log(string message) => AppLog.Write(message);   // central rotating log (10-01)

    /// <summary>DPI of the monitor the taskbar is on (0 if there's no taskbar right now).</summary>
    private static uint TaskbarDpi()
    {
        var taskbar = TaskbarHost.FindTaskbar();
        return taskbar == IntPtr.Zero ? 0 : GetDpiForWindow(taskbar);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);

    public void Dispose()
    {
        _stop.Cancel();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _stop.Dispose();
    }
}
