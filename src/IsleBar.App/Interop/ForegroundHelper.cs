using System.Runtime.InteropServices;

namespace IsleBar.App.Interop;

/// <summary>
/// Reliably brings our window to the front. Windows blocks <c>SetForegroundWindow</c>
/// while another program (e.g. Chrome) is in front — invoked via the global hotkey (Ctrl+Alt+C), the input box opened
/// but the keyboard kept going to Chrome (measured on PC 09-29). We get around it with the widely used trick of
/// briefly attaching to the foreground window's input thread (AttachThreadInput).
/// </summary>
internal static class ForegroundHelper
{
    public static void Bring(IntPtr window)
    {
        var foreground = GetForegroundWindow();
        if (foreground == window)
        {
            return;
        }

        var ours = GetCurrentThreadId();
        var theirs = foreground == IntPtr.Zero ? 0u : GetWindowThreadProcessId(foreground, IntPtr.Zero);
        var attached = theirs != 0 && theirs != ours && AttachThreadInput(ours, theirs, true);
        try
        {
            BringWindowToTop(window);
            if (!SetForegroundWindow(window))
            {
                // If still blocked: send one Alt key signal so it counts as "the user just pressed a key", then retry (common workaround),
                // and finally SwitchToThisWindow (same mechanism as task switching). Clicking our window inside the taskbar
                // was blocked because the foreground window was Explorer (measured on PC 09-30: clicking the island to go to the terminal).
                keybd_event(0x12, 0, 0, UIntPtr.Zero);
                keybd_event(0x12, 0, 2, UIntPtr.Zero);
                if (!SetForegroundWindow(window))
                {
                    SwitchToThisWindow(window, true);
                }
            }

            SetFocus(window);
        }
        finally
        {
            if (attached)
            {
                AttachThreadInput(ours, theirs, false);
            }
        }
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, IntPtr processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern IntPtr SetFocus(IntPtr window);

    [DllImport("user32.dll")]
    private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    [DllImport("user32.dll")]
    private static extern void SwitchToThisWindow(IntPtr window, [MarshalAs(UnmanagedType.Bool)] bool altTab);
}
