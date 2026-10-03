using System.Runtime.InteropServices;

namespace IsleBar.App.Interop;

/// <summary>
/// Global hotkey Ctrl+Alt+C — type into the search box instantly from anywhere (same key as the Python version).
/// Receives WM_HOTKEY on a separate thread and calls <see cref="Pressed"/> (marshalling to the UI thread is the receiver's job).
/// If another program already uses it, silently gives up.
/// </summary>
internal sealed class GlobalHotkey : IDisposable
{
    private const int Id = 1;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModNoRepeat = 0x4000;
    private const uint VkC = 0x43;
    private const uint WmHotkey = 0x0312;
    private const uint WmQuit = 0x0012;

    private readonly Thread _thread;
    private uint _threadId;

    public GlobalHotkey(Action pressed)
    {
        Pressed = pressed;
        _thread = new Thread(Loop) { IsBackground = true, Name = "IsleBar hotkey" };
        _thread.Start();
    }

    public Action Pressed { get; }

    /// <summary>Whether registration succeeded (false if another program uses Ctrl+Alt+C).</summary>
    public bool Registered { get; private set; }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        Registered = RegisterHotKey(IntPtr.Zero, Id, ModAlt | ModControl | ModNoRepeat, VkC);
        if (!Registered)
        {
            AppLog.Write($"hotkey: Ctrl+Alt+C is taken by another program (error {Marshal.GetLastWin32Error()})");   // silent before (review 10-03)
            return;
        }

        try
        {
            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.message == WmHotkey && (int)msg.wParam == Id)
                {
                    Pressed();
                }
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, Id);
        }
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr window, int id);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG message, IntPtr window, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
