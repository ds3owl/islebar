using System.Runtime.InteropServices;

namespace IsleBar.App.Interop;

/// <summary>
/// Win32 declarations. <b>The code in this folder has never been built on the server (Linux).</b>
/// Needs phase-0 verification on PC — see docs/PHASE0_CHECKLIST.md.
/// </summary>
internal static partial class NativeMethods
{
    // ---- Quiet time / full screen (for the chime, 10-03) ----
    internal const int QUNS_BUSY = 2;                     // a full-screen app
    internal const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;  // a full-screen game
    internal const int QUNS_PRESENTATION_MODE = 4;
    internal const int QUNS_QUIET_TIME = 6;               // quiet hours (not what Windows 11's Do Not Disturb reports — see below)

    [DllImport("shell32.dll")]
    internal static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);

    /// <summary>Whether a mouse button (left, right or middle) is held down right now.</summary>
    internal static bool MouseButtonDown()
        => (GetAsyncKeyState(0x01) & 0x8000) != 0 || (GetAsyncKeyState(0x02) & 0x8000) != 0 || (GetAsyncKeyState(0x04) & 0x8000) != 0;

    /// <summary>
    /// Windows 11's Do Not Disturb (also turned on by a focus session) — the notification state above still says "accepts
    /// notifications" while it is on (seen on PC 10-03). The shell publishes the active quiet-hours profile in this WNF state:
    /// 0 off, 1 priority only (Do Not Disturb), 2 alarms only. It is undocumented, so any failure reads as "off".
    /// </summary>
    internal static bool DoNotDisturbOn()
    {
        try
        {
            ulong name = 0x0D83063EA3BF1C75;   // WNF_SHEL_QUIETHOURS_ACTIVE_PROFILE_CHANGED
            uint stamp, size = 4;
            int value;
            return NtQueryWnfStateData(ref name, IntPtr.Zero, IntPtr.Zero, out stamp, out value, ref size) == 0 && size >= 4 && value != 0;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQueryWnfStateData(ref ulong stateName, IntPtr typeId, IntPtr scope, out uint changeStamp, out int buffer, ref uint bufferSize);

    // ---- Keyboard ----
    internal const byte VK_LWIN = 0x5B;
    internal const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    internal static extern void keybd_event(byte vk, byte scan, uint flags, IntPtr extraInfo);

    // ---- Window styles ----
    internal const int GWL_STYLE = -16;
    internal const int GWL_EXSTYLE = -20;

    internal const uint WS_POPUP = 0x8000_0000;
    internal const uint WS_CHILD = 0x4000_0000;
    internal const uint WS_CLIPSIBLINGS = 0x0400_0000;

    internal const uint WS_EX_TOPMOST = 0x0000_0008;
    internal const uint WS_EX_TOOLWINDOW = 0x0000_0080;
    internal const uint WS_EX_APPWINDOW = 0x0004_0000;
    internal const uint WS_EX_LAYERED = 0x0008_0000;
    internal const uint WS_EX_NOACTIVATE = 0x0800_0000;

    // ---- SetWindowPos flags ----
    internal const uint SWP_NOSIZE = 0x0001;
    internal const uint SWP_NOMOVE = 0x0002;
    internal const uint SWP_NOZORDER = 0x0004;
    internal const uint SWP_FRAMECHANGED = 0x0020;
    internal const uint SWP_SHOWWINDOW = 0x0040;
    internal const uint SWP_NOACTIVATE = 0x0010;

    internal static readonly IntPtr HWND_TOPMOST = new(-1);
    internal static readonly IntPtr HWND_TOP = IntPtr.Zero;

    internal const uint GA_ROOT = 2;
    internal const uint LWA_ALPHA = 0x2;

    // ---- DWM ----
    internal const int DWMWA_NCRENDERING_POLICY = 2;
    internal const int DWMNCRP_DISABLED = 1;   // turns off the non-client (DWM) rendering incl. the drop shadow
    internal const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    internal const int DWMWA_BORDER_COLOR = 34;
    internal const int DWMWCP_DONOTROUND = 1;
    internal const int DWMWCP_ROUND = 2;
    internal const uint DWMWA_COLOR_NONE = 0xFFFF_FFFE;

    // ---- DPI ----
    internal static readonly IntPtr DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = new(-4);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial IntPtr FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", SetLastError = true)]
    internal static partial IntPtr SetParent(IntPtr child, IntPtr newParent);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetParent(IntPtr window);

    internal const int SW_HIDE = 0;
    internal const int SW_SHOWNOACTIVATE = 4;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool ShowWindow(IntPtr window, int command);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetAncestor(IntPtr window, uint flags);

    /// <summary>
    /// <b>Return value and argument are signed 32-bit.</b> Passing a uint truncates at the top bit (WS_POPUP)
    /// and scrambles the style — actually hit on 09-29. Always pass through <see cref="Signed32"/>.
    /// </summary>
    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongW")]
    internal static partial int GetWindowLong(IntPtr window, int index);

    [LibraryImport("user32.dll", EntryPoint = "SetWindowLongW")]
    internal static partial int SetWindowLong(IntPtr window, int index, int newValue);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetWindowPos(
        IntPtr window, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(IntPtr window, out RECT rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetCursorPos(out POINT point);

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetLayeredWindowAttributes(IntPtr window, uint colorKey, byte alpha, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial int SetWindowRgn(IntPtr window, IntPtr region, [MarshalAs(UnmanagedType.Bool)] bool redraw);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindow(IntPtr window);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetForegroundWindow(IntPtr window);

    /// <summary>Child windows don't get focus from a normal click → must take it explicitly on click.</summary>
    [LibraryImport("user32.dll")]
    internal static partial IntPtr SetFocus(IntPtr window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetProcessDpiAwarenessContext(IntPtr context);

    [LibraryImport("user32.dll")]
    internal static partial IntPtr GetThreadDpiAwarenessContext();

    [LibraryImport("user32.dll")]
    internal static partial int GetAwarenessFromDpiAwarenessContext(IntPtr context);

    [LibraryImport("user32.dll")]
    internal static partial uint GetDpiForSystem();

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SystemParametersInfo(uint action, uint param, ref RECT data, uint winIni);

    internal const uint SPI_GETWORKAREA = 0x0030;

    [LibraryImport("user32.dll")]
    internal static partial int GetSystemMetrics(int index);

    internal const int SM_CYSCREEN = 1;

    // ---- Sound (winmm) ----
    internal const uint SND_ASYNC = 0x0001;       // return immediately, play in the background
    internal const uint SND_NODEFAULT = 0x0002;   // if the named sound is missing, play nothing (not the generic beep)
    internal const uint SND_ALIAS = 0x0001_0000;  // the name is a registered system-event alias (e.g. "Notification.Default")

    /// <summary>Plays a system sound. With <see cref="SND_ALIAS"/>, <paramref name="sound"/> is an AppEvents alias like "Notification.Default".</summary>
    [LibraryImport("winmm.dll", EntryPoint = "PlaySoundW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool PlaySound(string? sound, IntPtr module, uint flags);

    /// <summary>The last-resort beep when no notification sound is registered.</summary>
    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool MessageBeep(uint type);

    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    [LibraryImport("dwmapi.dll", EntryPoint = "DwmSetWindowAttribute")]
    internal static partial int DwmSetWindowAttributeUInt(IntPtr window, int attribute, ref uint value, int size);

    /// <summary>Extends the glass frame into the whole client area (margins all -1) so the window can be per-pixel transparent where the content is not painted.</summary>
    [LibraryImport("dwmapi.dll")]
    internal static partial int DwmExtendFrameIntoClientArea(IntPtr window, in MARGINS margins);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    internal struct MARGINS
    {
        public int Left;
        public int Right;
        public int Top;
        public int Bottom;
    }

    [LibraryImport("gdi32.dll")]
    internal static partial IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom, int widthEllipse, int heightEllipse);

    /// <summary>RGN_OR — combines two regions into their union (used to clip the window to the message deck silhouette).</summary>
    internal const int RGN_OR = 2;

    [LibraryImport("gdi32.dll")]
    internal static partial int CombineRgn(IntPtr dest, IntPtr src1, IntPtr src2, int mode);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DeleteObject(IntPtr obj);

    /// <summary>Gives the process its own app ID so the taskbar does not group it with another app's icon.</summary>
    [LibraryImport("shell32.dll", EntryPoint = "SetCurrentProcessExplicitAppUserModelID", StringMarshalling = StringMarshalling.Utf16)]
    internal static partial int SetCurrentProcessExplicitAppUserModelID(string appId);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateMutexW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    internal static partial IntPtr CreateMutex(
        IntPtr attributes, [MarshalAs(UnmanagedType.Bool)] bool initialOwner, string name);

    internal const int ERROR_ALREADY_EXISTS = 183;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(IntPtr handle);

    /// <summary>Converts a uint style value to the signed 32-bit value SetWindowLong accepts.</summary>
    internal static int Signed32(uint value) => unchecked((int)value);

    /// <summary>Converts a GetWindowLong result to a uint for bit operations.</summary>
    internal static uint Unsigned32(int value) => unchecked((uint)value);

    [StructLayout(LayoutKind.Sequential)]
    internal struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width => Right - Left;

        public int Height => Bottom - Top;
    }
}
