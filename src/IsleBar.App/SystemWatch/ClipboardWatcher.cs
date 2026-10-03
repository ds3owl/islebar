using System.Diagnostics;
using System.Runtime.InteropServices;
using IsleBar.Core.SystemWatch;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Copy confirmation: text → "Copied · first 24 chars" for 1.5 s, image → "Image copied", files → "3 files copied".
/// <para>
/// Why not WinRT <c>Clipboard.ContentChanged</c>: it must be subscribed on the UI thread (dispatcher), and
/// unpackaged apps can read the content only while their window is in front. Instead we hook
/// <c>AddClipboardFormatListener</c> on a <b>message-only window on a dedicated thread</b>, receive WM_CLIPBOARDUPDATE and read via Win32 directly —
/// independent of the UI thread, so <see cref="Start"/> can be called from any thread.
/// </para>
/// <para>
/// Passwords: password managers mark copies as "don't record" (<c>ExcludeClipboardContentFromMonitorProcessing</c>,
/// <c>CanIncludeInClipboardHistory</c>=0, <c>Clipboard Viewer Ignore</c>) — then we don't read the content and show "••••••".
/// Even without the mark, it's masked if the copying program is a known manager or the text looks like a password (<see cref="ClipboardRules"/>).
/// </para>
/// Browsers send WM_CLIPBOARDUPDATE several times per copy, so we coalesce for 150 ms and read only once.
/// </summary>
internal sealed class ClipboardWatcher : NoticeWatcher
{
    private const string Key = "clipboard";
    private static readonly TimeSpan Show = TimeSpan.FromSeconds(1.5);
    private const uint CoalesceMs = 150;
    private const int MaxTextChars = 4096;

    private const uint WmClipboardUpdate = 0x031D;
    private const uint WmTimer = 0x0113;
    private const uint WmQuit = 0x0012;
    private const uint CfBitmap = 2;
    private const uint CfDib = 8;
    private const uint CfUnicodeText = 13;
    private const uint CfHdrop = 15;
    private const uint CfDibV5 = 17;
    private static readonly IntPtr HwndMessage = new(-3);
    private static readonly UIntPtr TimerId = new(1);

    private readonly uint _formatExclude = RegisterClipboardFormat("ExcludeClipboardContentFromMonitorProcessing");
    private readonly uint _formatHistory = RegisterClipboardFormat("CanIncludeInClipboardHistory");
    private readonly uint _formatViewerIgnore = RegisterClipboardFormat("Clipboard Viewer Ignore");
    private readonly uint _formatPng = RegisterClipboardFormat("PNG");

    private Thread? _thread;
    private uint _threadId;
    private WndProc? _proc;   // keep a reference so the GC doesn't collect it while native code calls it
    private uint _lastSequence;

    public ClipboardWatcher(Func<Core.Localization.LanguageStrings> strings)
        : base(strings)
    {
    }

    public override void Start() => Guard(() =>
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "IsleBar clipboard" };
        _thread.Start();
    });

    protected override void Stop()
    {
        if (_threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, UIntPtr.Zero, IntPtr.Zero);
        }
    }

    private void Loop()
    {
        _threadId = GetCurrentThreadId();
        _proc = WindowProc;
        var className = "IsleBarClipboardWatch_" + Environment.ProcessId;
        var hInstance = GetModuleHandle(null);
        var wc = new WndClassEx
        {
            cbSize = (uint)Marshal.SizeOf<WndClassEx>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(_proc),
            hInstance = hInstance,
            lpszClassName = className,
        };

        if (RegisterClassEx(ref wc) == 0)
        {
            return;
        }

        var hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            UnregisterClass(className, hInstance);
            return;
        }

        try
        {
            _lastSequence = GetClipboardSequenceNumber();   // don't notify what was already there at startup
            if (!AddClipboardFormatListener(hwnd) || IsDisposed)
            {
                return;
            }

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        finally
        {
            RemoveClipboardFormatListener(hwnd);
            DestroyWindow(hwnd);
            UnregisterClass(className, hInstance);
        }
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam)
    {
        if (msg == WmClipboardUpdate)
        {
            SetTimer(hwnd, TimerId, CoalesceMs, IntPtr.Zero);   // even if it fires several times, only the last one counts
            return IntPtr.Zero;
        }

        if (msg == WmTimer && wParam == TimerId)
        {
            KillTimer(hwnd, TimerId);
            Guard(() => ReadAndAnnounce(hwnd));
            return IntPtr.Zero;
        }

        return DefWindowProc(hwnd, msg, wParam, lParam);
    }

    private void ReadAndAnnounce(IntPtr hwnd)
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _lastSequence)
        {
            return;
        }

        _lastSequence = sequence;
        var owner = OwnerProcessName();
        if (Read(hwnd, owner) is not { } notice)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        Board.Flash(Key, SystemNotice.Make(notice.Title, notice.Glyph, now), Show, now);
    }

    private (string Title, string Glyph)? Read(IntPtr hwnd, string? owner)
    {
        // If another program holds the clipboard, wait briefly and retry (this thread is ours, so waiting is fine)
        var opened = false;
        for (var i = 0; i < 5 && !opened; i++)
        {
            opened = OpenClipboard(hwnd);
            if (!opened)
            {
                Thread.Sleep(30);
            }
        }

        if (!opened)
        {
            return null;
        }

        try
        {
            var s = Text;
            if (IsFlaggedSecret() || ClipboardRules.IsPasswordManager(owner))
            {
                return (NoticeText.Copied(s, NoticeText.Masked), NoticeGlyphs.Copy);
            }

            if (IsClipboardFormatAvailable(CfHdrop) && GetClipboardData(CfHdrop) is var drop && drop != IntPtr.Zero)
            {
                var count = (int)DragQueryFile(drop, 0xFFFF_FFFF, null, 0);
                return count > 0 ? (NoticeText.CopiedFiles(s, count), NoticeGlyphs.Files) : null;
            }

            if (IsClipboardFormatAvailable(CfUnicodeText) && ReadText() is { } text)
            {
                var preview = ClipboardRules.Preview(text);
                return preview.Length == 0 ? null : (NoticeText.Copied(s, preview), NoticeGlyphs.Copy);
            }

            if (IsClipboardFormatAvailable(CfDib) || IsClipboardFormatAvailable(CfDibV5)
                || IsClipboardFormatAvailable(CfBitmap) || (_formatPng != 0 && IsClipboardFormatAvailable(_formatPng)))
            {
                return (s.NoticeCopiedImage, NoticeGlyphs.Image);
            }

            return null;   // other formats (app-private data) aren't notified
        }
        finally
        {
            CloseClipboard();
        }
    }

    /// <summary>Whether the "don't record" mark added by password managers is present.</summary>
    private bool IsFlaggedSecret()
    {
        if ((_formatExclude != 0 && IsClipboardFormatAvailable(_formatExclude))
            || (_formatViewerIgnore != 0 && IsClipboardFormatAvailable(_formatViewerIgnore)))
        {
            return true;
        }

        // CanIncludeInClipboardHistory = DWORD 0 means "don't record"
        if (_formatHistory != 0 && IsClipboardFormatAvailable(_formatHistory))
        {
            var handle = GetClipboardData(_formatHistory);
            var ptr = handle == IntPtr.Zero ? IntPtr.Zero : GlobalLock(handle);
            if (ptr != IntPtr.Zero)
            {
                try
                {
                    return GlobalSize(handle) >= 4 && Marshal.ReadInt32(ptr) == 0;
                }
                finally
                {
                    GlobalUnlock(handle);
                }
            }
        }

        return false;
    }

    private static string? ReadText()
    {
        var handle = GetClipboardData(CfUnicodeText);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        var ptr = GlobalLock(handle);
        if (ptr == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var chars = (int)Math.Min((long)GlobalSize(handle) / 2, MaxTextChars);
            var text = Marshal.PtrToStringUni(ptr, chars);
            var nul = text.IndexOf('\0', StringComparison.Ordinal);
            return nul >= 0 ? text[..nul] : text;
        }
        finally
        {
            GlobalUnlock(handle);
        }
    }

    /// <summary>Name of the program that last put data on the clipboard (for password-manager detection). Null if unknown.</summary>
    private static string? OwnerProcessName()
    {
        var owner = GetClipboardOwner();
        if (owner == IntPtr.Zero || GetWindowThreadProcessId(owner, out var pid) == 0 || pid == 0)
        {
            return null;
        }

        try
        {
            using var process = Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    // ---- Win32 ----

    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WndClassEx
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WndClassEx wc);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string className, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint exStyle, string className, string windowName, uint style, int x, int y, int width, int height,
        IntPtr parent, IntPtr menu, IntPtr hInstance, IntPtr param);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hwnd, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg msg, IntPtr hwnd, uint min, uint max);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg msg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint threadId, uint msg, UIntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint elapseMs, IntPtr timerProc);

    [DllImport("user32.dll")]
    private static extern bool KillTimer(IntPtr hwnd, UIntPtr id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool OpenClipboard(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    private static extern bool IsClipboardFormatAvailable(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardData(uint format);

    [DllImport("user32.dll")]
    private static extern IntPtr GetClipboardOwner();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterClipboardFormat(string name);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern uint DragQueryFile(IntPtr drop, uint index, char[]? file, uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GlobalLock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern bool GlobalUnlock(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern UIntPtr GlobalSize(IntPtr handle);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
