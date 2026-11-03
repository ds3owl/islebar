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
                    if (!PassAltGrCharacter())
                    {
                        Pressed();
                    }
                }
            }
        }
        finally
        {
            UnregisterHotKey(IntPtr.Zero, Id);
        }
    }

    /// <summary>
    /// AltGr counts as Ctrl+Alt, so on layouts with an AltGr+C character (Polish "ć", and others) the hotkey took that key and
    /// the character couldn't be typed anywhere (review 11-03). When the hotkey came with the right Alt key down and the
    /// foreground window's layout has a character there, that character is typed instead and the bar isn't opened.
    /// </summary>
    private static bool PassAltGrCharacter()
    {
        if (GetAsyncKeyState(VkRMenu) >= 0)
        {
            return false;   // left Alt: the real Ctrl+Alt+C
        }

        var foreground = GetForegroundWindow();
        var layout = GetKeyboardLayout(GetWindowThreadProcessId(foreground, out _));
        var state = new byte[256];
        state[VkControl] = state[VkLControl] = state[VkMenu] = state[VkRMenu] = 0x80;
        var text = new System.Text.StringBuilder(8);
        var scan = MapVirtualKeyEx(VkC, 0 /* MAPVK_VK_TO_VSC */, layout);
        var count = ToUnicodeEx(VkC, scan, state, text, text.Capacity, 0x4 /* don't change the keyboard state */, layout);
        if (count <= 0 || text.Length == 0 || char.IsControl(text[0]))
        {
            return false;   // nothing there on this layout (Korean, English…): it is the hotkey
        }

        var inputs = new List<INPUT>();
        foreach (var c in text.ToString(0, count))
        {
            inputs.Add(new INPUT { Type = 1, Key = new KEYBDINPUT { Scan = c, Flags = KeyEventUnicode } });
            inputs.Add(new INPUT { Type = 1, Key = new KEYBDINPUT { Scan = c, Flags = KeyEventUnicode | KeyEventKeyUp } });
        }

        SendInput((uint)inputs.Count, [.. inputs], Marshal.SizeOf<INPUT>());
        return true;
    }

    private const int VkControl = 0x11;
    private const int VkMenu = 0x12;
    private const int VkLControl = 0xA2;
    private const int VkRMenu = 0xA5;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint KeyEventUnicode = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public IntPtr Extra;
    }

    [StructLayout(LayoutKind.Explicit, Size = 40)]   // the size of the INPUT union on x64 (its MOUSEINPUT member is the largest)
    private struct INPUT
    {
        [FieldOffset(0)]
        public uint Type;
        [FieldOffset(8)]
        public KEYBDINPUT Key;
    }

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKeyEx(uint code, uint mapType, IntPtr layout);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int ToUnicodeEx(uint key, uint scan, byte[] state, System.Text.StringBuilder text, int size, uint flags, IntPtr layout);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint count, INPUT[] inputs, int size);

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
