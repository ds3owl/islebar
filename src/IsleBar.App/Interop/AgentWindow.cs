using System.Runtime.InteropServices;

namespace IsleBar.App.Interop;

/// <summary>
/// Brings forward the terminal window where an agent process (claude.exe etc.) runs (when the island's Claude notice is clicked —
/// decision card "clicking goes to that terminal"). Briefly attaches to that process's console to get the console window,
/// and if it's Windows Terminal, brings forward that console window's owner (the terminal window). Returns false if not found.
/// </summary>
internal static class AgentWindow
{
    private const uint GA_ROOTOWNER = 3;

    public static bool Bring(int pid)
    {
        var target = Find(pid);
        if (target == IntPtr.Zero)
        {
            return false;
        }

        if (IsIconic(target))
        {
            ShowWindow(target, 9);   // SW_RESTORE
        }

        ForegroundHelper.Bring(target);
        return true;
    }

    /// <summary>
    /// The terminal window an agent process runs in (the Windows Terminal window that owns its console, or the console itself),
    /// or zero if the process is gone or has no console.
    /// </summary>
    public static IntPtr Find(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            if (process.HasExited)
            {
                return IntPtr.Zero;
            }
        }
        catch (ArgumentException)
        {
            return IntPtr.Zero;   // session already ended
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // an agent running as administrator can't be inspected: its finished task froze the whole pill — the exception
            // escaped the tick every time (review 10-03). Its window just isn't found.
            return IntPtr.Zero;
        }

        if (!AttachConsole((uint)pid))
        {
            return IntPtr.Zero;
        }

        IntPtr console;
        try
        {
            console = GetConsoleWindow();
        }
        finally
        {
            FreeConsole();
        }

        if (console == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        var owner = GetAncestor(console, GA_ROOTOWNER);
        var target = owner != IntPtr.Zero ? owner : console;
        var cls = new System.Text.StringBuilder(64);
        GetClassName(target, cls, 64);
        var visible = IsWindowVisible(target);
        TaskbarHost.Log($"find terminal: pid={pid} console={console:X} owner={owner:X} target={target:X} class={cls} visible={visible}");
        return visible ? target : IntPtr.Zero;   // a hidden pseudo console (editor terminals) never gets clicks — don't watch it
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FreeConsole();

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr window, System.Text.StringBuilder name, int max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr window, int command);
}
