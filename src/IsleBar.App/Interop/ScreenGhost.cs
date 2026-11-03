using System.Runtime.InteropServices;

namespace IsleBar.App.Interop;

/// <summary>
/// A still picture of part of the screen, laid over it for a few frames while a window there changes in a way that reaches the
/// screen in steps. Takes no clicks and no focus; then it goes and the real window shows, already drawn.
/// <list type="bullet">
/// <item><see cref="Pill"/>: the pill taken out of the taskbar or put back. Changing the window's parent reaches the screen in
/// steps (new parent, then new place, then a freshly bound surface), and in between the pill was gone for a frame at typing
/// start or end (60 fps capture, 3 of 6 starts, 11-03).</item>
/// <item><see cref="Popup"/>: a popup above the bar resized or first placed on screen. The window reaches the screen at its new
/// size a few frames before XAML has drawn it there: a see-through or white band under the old text when a line wrapped,
/// or an empty outline when it first appeared (user 11-03).</item>
/// </list>
/// </summary>
internal sealed class ScreenGhost
{
    public static readonly ScreenGhost Pill = new(4);
    // A popup resized on screen was once still a frame short of drawn after 4 ticks (compose box, 60 fps capture 11-03)
    public static readonly ScreenGhost Popup = new(6);

    private readonly int _framesShown;   // Rendering ticks to wait: a tick comes before its frame, and the window needs a few

    private ScreenGhost(int framesShown) => _framesShown = framesShown;

    private IntPtr _window;
    private int _frames;
    private bool _waiting;

    /// <summary>Covers the window <paramref name="window"/> (an outer handle) with a picture of what is on screen there now.</summary>
    public void Cover(IntPtr window)
    {
        if (IsWindowVisible(window) && NativeMethods.GetWindowRect(window, out var r))
        {
            Cover(r.Left, r.Top, r.Right, r.Bottom);
        }
    }

    /// <summary>Covers the screen rectangle with a picture of what is on screen there now.</summary>
    public void Cover(int left, int top, int right, int bottom)
    {
        try
        {
            if (right <= left || bottom <= top)
            {
                return;
            }

            var r = new NativeMethods.RECT { Left = left, Top = top, Right = right, Bottom = bottom };
            int width = r.Right - r.Left, height = r.Bottom - r.Top;
            if (_window == IntPtr.Zero || !IsWindow(_window))
            {
                _window = CreateWindowExW(
                    WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
                    "Static", null, WS_POPUP, r.Left, r.Top, width, height, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
                if (_window == IntPtr.Zero)
                {
                    return;
                }
            }

            var screen = GetDC(IntPtr.Zero);
            var memory = CreateCompatibleDC(screen);
            var bitmap = CreateCompatibleBitmap(screen, width, height);
            var old = SelectObject(memory, bitmap);
            var copied = BitBlt(memory, 0, 0, width, height, screen, r.Left, r.Top, SRCCOPY);
            if (copied)
            {
                var at = new POINT { X = r.Left, Y = r.Top };
                var size = new SIZE { Cx = width, Cy = height };
                var origin = new POINT();
                var blend = new BLENDFUNCTION { SourceConstantAlpha = 255 };   // opaque: the per-pixel alpha of a screen copy means nothing
                UpdateLayeredWindow(_window, screen, ref at, ref size, memory, ref origin, 0, ref blend, ULW_ALPHA);
                NativeMethods.SetWindowPos(_window, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
            }

            SelectObject(memory, old);
            DeleteObject(bitmap);
            DeleteDC(memory);
            ReleaseDC(IntPtr.Zero, screen);
            if (!copied)
            {
                return;
            }

            _frames = 0;
            if (!_waiting)
            {
                _waiting = true;
                Microsoft.UI.Xaml.Media.CompositionTarget.Rendering += OnRendering;
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            AppLog.Write($"screen ghost: {e.Message}");
        }
    }

    /// <summary>Back on top after the covered window itself was raised (a detached pill is made topmost).</summary>
    public void Raise()
    {
        if (_waiting && _window != IntPtr.Zero)
        {
            NativeMethods.SetWindowPos(_window, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }
    }

    private void OnRendering(object? sender, object e)
    {
        if (++_frames < _framesShown)
        {
            return;
        }

        Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
        _waiting = false;
        ShowWindow(_window, 0 /* SW_HIDE */);
    }

    public void Destroy()
    {
        if (_waiting)
        {
            Microsoft.UI.Xaml.Media.CompositionTarget.Rendering -= OnRendering;
            _waiting = false;
        }

        if (_window != IntPtr.Zero)
        {
            DestroyWindow(_window);
            _window = IntPtr.Zero;
        }
    }

    private const uint WS_POPUP = 0x80000000;
    private const uint WS_EX_LAYERED = 0x00080000;
    private const uint WS_EX_TRANSPARENT = 0x00000020;
    private const uint WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WS_EX_TOPMOST = 0x00000008;
    private const uint WS_EX_NOACTIVATE = 0x08000000;
    private const uint SRCCOPY = 0x00CC0020;
    private const uint ULW_ALPHA = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int Cx;
        public int Cy;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp;
        public byte BlendFlags;
        public byte SourceConstantAlpha;
        public byte AlphaFormat;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string? name, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr window);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr window, int command);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr dc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr dc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height, IntPtr source, int sx, int sy, uint rop);

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr window, IntPtr screenDc, ref POINT at, ref SIZE size, IntPtr sourceDc, ref POINT source, uint key, ref BLENDFUNCTION blend, uint flags);
}
