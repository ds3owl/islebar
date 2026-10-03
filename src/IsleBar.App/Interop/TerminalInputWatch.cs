using System.Runtime.InteropServices;

namespace IsleBar.App.Interop;

/// <summary>
/// Tells whether the user actually touched a given terminal window — a mouse click on it, or a key pressed while it is in front.
/// Used to clear a finished task's green border only once you've really gone back to that terminal (user 10-01): "being in front"
/// alone isn't proof you saw it (it may have been left open while you stepped away), and plain mouse movement doesn't count.
/// <para>
/// Low-level mouse/keyboard hooks run on their own thread and <b>only while there is a window to watch</b> (a finished task is up).
/// Nothing about the input is kept — not which key, not where — only "that window got input".
/// </para>
/// </summary>
internal sealed class TerminalInputWatch : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const uint WM_QUIT = 0x0012;
    private const uint GA_ROOT = 2;

    private const uint PM_NOREMOVE = 0;

    private readonly object _gate = new();
    private readonly object _life = new();   // Start / Stop never interleave
    private readonly HashSet<IntPtr> _watched = [];
    private readonly HashSet<IntPtr> _touched = [];
    private Thread? _thread;
    private uint _threadId;
    private ManualResetEventSlim? _ready;

    /// <summary>Sets the windows to watch; hooks are installed while this is non-empty and removed when it's empty.</summary>
    public void Watch(IReadOnlyCollection<IntPtr> windows)
    {
        lock (_gate)
        {
            _watched.Clear();
            _watched.UnionWith(windows.Where(w => w != IntPtr.Zero));
            _touched.IntersectWith(_watched);
        }

        if (windows.Count > 0)
        {
            Start();
        }
        else
        {
            Stop();
        }
    }

    /// <summary>True (once) if <paramref name="window"/> got a click or a key press since the last call.</summary>
    public bool TakeTouched(IntPtr window)
    {
        lock (_gate)
        {
            return _touched.Remove(window);
        }
    }

    public void Dispose() => Stop();

    private void Start()
    {
        lock (_life)
        {
            if (_thread is not null)
            {
                return;
            }

            var ready = new ManualResetEventSlim(false);
            HookProc mouse = OnMouse, key = OnKey;   // this thread's own delegates, alive until it has unhooked
            var thread = new Thread(() =>
            {
                // The message queue exists before Stop can post to it — a WM_QUIT sent to a thread without one was lost, leaving
                // the old hooks running while a new Start replaced their delegates (race found 10-02, fixed 10-03).
                PeekMessage(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);
                _threadId = GetCurrentThreadId();
                ready.Set();
                var module = GetModuleHandle(null);
                var mouseHook = SetWindowsHookExW(WH_MOUSE_LL, mouse, module, 0);
                var keyHook = SetWindowsHookExW(WH_KEYBOARD_LL, key, module, 0);
                while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0)
                {
                }

                UnhookWindowsHookEx(mouseHook);
                UnhookWindowsHookEx(keyHook);
                GC.KeepAlive(mouse);
                GC.KeepAlive(key);
            })
            { IsBackground = true, Name = "IsleBar terminal input" };
            _ready = ready;
            _thread = thread;
            thread.Start();
        }
    }

    private void Stop()
    {
        lock (_life)
        {
            if (_thread is null)
            {
                return;
            }

            _ready?.Wait(1000);   // a Stop right after Start: wait until the thread has a queue to post to
            for (var i = 0; i < 20 && !PostThreadMessage(_threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); i++)   // ends its message loop → it unhooks
            {
                Thread.Sleep(10);
            }

            _thread.Join(1000);
            _thread = null;
            _threadId = 0;
            _ready?.Dispose();
            _ready = null;
        }
    }

    private IntPtr OnMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN)
        {
            var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            Mark(GetAncestor(WindowFromPoint(info.pt), GA_ROOT));   // the click landed on (a part of) that terminal window
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);   // the hook handle argument is ignored
    }

    private IntPtr OnKey(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0 && (int)wParam is WM_KEYDOWN or WM_SYSKEYDOWN && !IsIncidentalKey(Marshal.ReadInt32(lParam)) && !SystemShortcutHeld())
        {
            Mark(GetForegroundWindow());   // typing goes to the window in front
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    /// <summary>
    /// Keys that don't mean "I'm using this terminal": modifiers on their own (Alt of Alt+Tab, Win, Shift, Ctrl) and the
    /// volume/media keys — pressing them while the terminal happens to be in front shouldn't clear its completion (code review 10-01).
    /// </summary>
    /// <summary>
    /// Alt or Win held: the key belongs to a system shortcut (Alt+Tab, Win+L, Win+D …) or IsleBar's own Ctrl+Alt+C, not to the
    /// terminal — stepping away with Win+L cleared a finished task's border (review 10-03). Ctrl+key still counts (Ctrl+C there is use).
    /// </summary>
    private static bool SystemShortcutHeld()
        => (GetAsyncKeyState(0x12) & 0x8000) != 0 || (GetAsyncKeyState(0x5B) & 0x8000) != 0 || (GetAsyncKeyState(0x5C) & 0x8000) != 0;

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    private static bool IsIncidentalKey(int vk)
        => vk is >= 0x10 and <= 0x12      // Shift, Ctrl, Alt
            or >= 0xA0 and <= 0xA5        // left/right Shift, Ctrl, Alt
            or 0x5B or 0x5C               // Win
            or >= 0xAD and <= 0xB7;       // volume, media, launch keys

    private void Mark(IntPtr window)
    {
        lock (_gate)
        {
            if (_watched.Contains(window))
            {
                _touched.Add(window);
            }
        }
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetModuleHandle(string? name);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr window, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
}
