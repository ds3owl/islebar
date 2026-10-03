using System.Runtime.InteropServices;
using static IsleBar.App.Interop.NativeMethods;

namespace IsleBar.App.Interop;

/// <summary>
/// Attaching the window into the taskbar (Shell_TrayWnd) and detaching it. **The core of the phase-0 test.**
///
/// Why a child window: a plain floating (topmost) window hides behind the taskbar when the Windows key
/// raises the taskbar to a higher layer. As a taskbar child it is drawn as part of the taskbar and never covered.
///
/// Reflects everything learned on 09-29:
///  1. SetParent only succeeds with process DPI = PER_MONITOR_AWARE_V2 (must match Explorer). → DpiSetup
///  2. The attach target is the outer window from <c>GetAncestor(GA_ROOT)</c>, not WinUI's inner window.
///  3. Pass SetWindowLong values as <b>signed 32-bit</b> (WS_POPUP top bit).
///  4. Toggle the layered style off and on right after adding it — rebinding the composition surface keeps us
///     visible on top even when the Start menu opens and the taskbar repaints.
///  5. <b>Detach while typing</b> (normal topmost window) — so IME and focus don't get entangled with Explorer.
///  6. On failure, restore the styles so it can be used as a plain topmost window.
///
/// <b>This file has never been built. Needs phase-0 verification on PC.</b>
/// </summary>
internal sealed class TaskbarHost(IntPtr windowHandle)
{
    /// <summary>Outer window of the WinUI window. Resolved once before attaching (the parent changes after).</summary>
    private readonly IntPtr _wrap = GetAncestor(windowHandle, GA_ROOT) is var root && root != IntPtr.Zero
        ? root
        : windowHandle;

    /// <summary>Title bar, sizing border, system menu, minimize/maximize buttons (none are needed inside the taskbar).</summary>
    private const uint FrameBits = 0x00C00000 | 0x00040000 | 0x00080000 | 0x00020000 | 0x00010000;

    /// <summary>Original values kept to restore the styles.</summary>
    private uint _originalStyle;
    private uint _originalExStyle;
    private bool _savedOriginal;

    /// <summary>Taskbar we are currently attached to. IntPtr.Zero if not attached.</summary>
    public IntPtr Host { get; private set; }

    /// <summary>Whether we are detached for input.</summary>
    public bool Detached { get; private set; }

    public bool IsAttached => Host != IntPtr.Zero && GetParent(_wrap) == Host;

    /// <summary>Finds the taskbar window. IntPtr.Zero if missing (Explorer is restarting).</summary>
    public static IntPtr FindTaskbar() => FindWindow("Shell_TrayWnd", null);

    /// <summary>
    /// Attaches to the taskbar. Returns true as-is if already attached.
    /// If Explorer restarts and the taskbar window changes, re-attaches to the new one.
    /// </summary>
    public bool Attach()
    {
        var tray = FindTaskbar();
        if (tray == IntPtr.Zero)
        {
            return false;
        }

        if (Host == tray && GetParent(_wrap) == tray)
        {
            return true;    // already attached
        }

        var exStyle = Unsigned32(GetWindowLong(_wrap, GWL_EXSTYLE));
        var style = Unsigned32(GetWindowLong(_wrap, GWL_STYLE));
        if (!_savedOriginal)
        {
            (_originalStyle, _originalExStyle, _savedOriginal) = (style, exStyle, true);
        }

        // Tool window + no-activate, TOPMOST off (it will become a child)
        SetWindowLong(_wrap, GWL_EXSTYLE,
            Signed32((exStyle | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE) & ~WS_EX_TOPMOST));
        // Make it a child window, POPUP off
        SetWindowLong(_wrap, GWL_STYLE,
            Signed32((style | WS_CHILD | WS_CLIPSIBLINGS) & ~(WS_POPUP | FrameBits)));

        Marshal.SetLastSystemError(0);
        SetParent(_wrap, tray);
        var error = Marshal.GetLastWin32Error();

        Host = GetParent(_wrap) == tray ? tray : IntPtr.Zero;

        if (Host == IntPtr.Zero)
        {
            // Failure (usually DPI mode differs from Explorer) → restore so it works as a plain topmost window
            SetWindowLong(_wrap, GWL_STYLE, Signed32(_originalStyle));
            SetWindowLong(_wrap, GWL_EXSTYLE, Signed32(_originalExStyle));
            Log($"attach failed tray={tray:X} wrap={_wrap:X} err={error} dpi={DpiSetup.CurrentAwareness()}");
            return false;
        }

        // Lesson 4: toggle layered off and on to rebind the composition surface
        var nowEx = Unsigned32(GetWindowLong(_wrap, GWL_EXSTYLE));
        SetWindowLong(_wrap, GWL_EXSTYLE, Signed32(nowEx & ~WS_EX_LAYERED));
        SetWindowLong(_wrap, GWL_EXSTYLE, Signed32(nowEx | WS_EX_LAYERED));
        SetLayeredWindowAttributes(_wrap, 0, 255, LWA_ALPHA);   // opaque

        Log($"attach ok tray={tray:X} wrap={_wrap:X} dpi={DpiSetup.CurrentAwareness()}");
        Detached = false;
        return true;
    }

    /// <summary>
    /// Detaches from the taskbar into a normal topmost window. <b>Call when input starts</b> —
    /// typing while attached entangles IME/focus with Explorer and can hang Explorer.
    /// </summary>
    public void Detach(int x, int y, int width, int height)
    {
        if (Detached || Host == IntPtr.Zero)
        {
            return;
        }

        SetParent(_wrap, IntPtr.Zero);

        var style = Unsigned32(GetWindowLong(_wrap, GWL_STYLE));
        SetWindowLong(_wrap, GWL_STYLE, Signed32((style & ~WS_CHILD) | WS_POPUP));

        var exStyle = Unsigned32(GetWindowLong(_wrap, GWL_EXSTYLE));
        SetWindowLong(_wrap, GWL_EXSTYLE, Signed32(exStyle & ~WS_EX_NOACTIVATE));   // allow activation

        Host = IntPtr.Zero;
        Detached = true;
        SetWindowPos(_wrap, HWND_TOPMOST, x, y, width, height, SWP_SHOWWINDOW | SWP_FRAMECHANGED);

        // Bring the detached window to the front — otherwise keystrokes after the first click leak to the previous foreground window (Explorer search etc.) (measured on PC 09-29).
        // Our process just received the click, so Windows allows it.
        ForegroundHelper.Bring(_wrap);
    }

    /// <summary>
    /// Before the window is closed: hide it, take it out of the taskbar and give back its original top-level styles.
    /// Closing a WinUI window resets its title bar, and on our re-parented child window that fails with ERROR_NOT_SUPPORTED —
    /// WinUI turns the failure into a fail-fast (0xC000027B at WindowChrome::SetTitleBar), so every quit and every self-restart
    /// (theme change, Explorer restart) ended as a crash and the watchdog brought the bar back even after "turn off" (dump 10-01).
    /// </summary>
    public void ReleaseForClose()
    {
        if (!IsWindow(_wrap))
        {
            return;
        }

        ShowWindow(_wrap, 0 /* SW_HIDE */);
        if (GetParent(_wrap) != IntPtr.Zero)
        {
            SetParent(_wrap, IntPtr.Zero);
        }

        if (_savedOriginal)
        {
            SetWindowLong(_wrap, GWL_STYLE, Signed32(_originalStyle));
            SetWindowLong(_wrap, GWL_EXSTYLE, Signed32(_originalExStyle));
        }

        Host = IntPtr.Zero;
        Detached = true;
        SetWindowPos(_wrap, IntPtr.Zero, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_FRAMECHANGED);
    }

    /// <summary>Places at screen coordinates (x, y), converted to taskbar-relative coordinates if a child.</summary>
    public void PlaceAt(int x, int y, int width, int height)
    {
        if (Host != IntPtr.Zero)
        {
            if (!GetWindowRect(Host, out var hostRect))
            {
                return;
            }

            var ok = SetWindowPos(_wrap, HWND_TOP, x - hostRect.Left, y - hostRect.Top, width, height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
            Log($"PlaceAt child {x - hostRect.Left},{y - hostRect.Top} {width}x{height} ok={ok} err={Marshal.GetLastWin32Error()}");
        }
        else
        {
            SetWindowPos(_wrap, Detached ? HWND_TOPMOST : HWND_TOP, x, y, width, height,
                SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }
    }

    /// <summary>
    /// Takes focus on click. <b>Child windows don't get focus from a normal click</b> →
    /// must be called explicitly on press for input to work.
    /// </summary>
    public void TakeFocus() => SetFocus(_wrap);

    /// <summary>Whether the window is still alive (for monitoring).</summary>
    public bool WindowAlive => IsWindow(_wrap);

    /// <summary>Outer window handle (used for region clipping, etc.).</summary>
    public IntPtr OuterWindow => _wrap;

    internal static void Log(string message) => AppLog.Write(message);   // central rotating log (10-01)
}
