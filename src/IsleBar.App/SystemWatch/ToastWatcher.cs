using System.Runtime.InteropServices;
using IsleBar.Core.Localization;
using IsleBar.Core.SystemWatch;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Pulls Windows notifications onto the island (user feedback 09-30: pull in Windows notifications). Notifications from any app, KakaoTalk, mail, etc.,
/// shown as "app name · content" for 6 s.
/// <para>
/// The official notification-reading API (UserNotificationListener) requires a signed package, so instead we read the database where Windows records
/// notifications in the user's profile (%LOCALAPPDATA%\Microsoft\Windows\Notifications\wpndatabase.db)
/// <b>read-only</b> every second with the SQLite that ships with Windows (winsqlite3.dll) — only toasts with new IDs.
/// Nothing is written, so the notification center is unaffected. Since it reads all notification content, it must be enabled in settings (off by default).
/// </para>
/// </summary>
internal sealed class ToastWatcher(Func<LanguageStrings> strings) : NoticeWatcher(strings)
{
    private static readonly TimeSpan Poll = TimeSpan.FromMilliseconds(400);   // check often for low latency; the DB is only opened when its file actually changed
    private static readonly TimeSpan Show = TimeSpan.FromSeconds(6);   // 4 s was over before a long notice had scrolled to its text
    private readonly CancellationTokenSource _stop = new();
    private readonly Dictionary<string, string> _names = [];
    private long _lastId = -1;
    private DateTime _lastDbWrite = DateTime.MinValue;
    private Thread? _thread;

    /// <summary>Option "only on the bar": each time a notification lands, also silence the banners of apps that appeared since (set by <see cref="SystemWatchers"/>).</summary>
    public volatile bool HideBanners;

    internal static string DatabasePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "Windows", "Notifications", "wpndatabase.db");

    public override void Start()
    {
        if (_thread is not null || !File.Exists(DatabasePath))
        {
            return;
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "IsleBar toasts" };
        _thread.Start();
    }

    private void Loop()
    {
        while (!_stop.Token.WaitHandle.WaitOne(Poll))
        {
            if (DatabaseChanged())   // a cheap stat gate — only open the SQLite file when a notification actually landed
            {
                Guard(Check);
                if (HideBanners)
                {
                    ToastBanners.Hide();   // a new app's first notice pops up once; from then on it's silenced too
                }
            }
        }
    }

    /// <summary>True when the notification DB (or its write-ahead log) has been written since the last check.</summary>
    private bool DatabaseChanged()
    {
        var latest = DateTime.MinValue;
        foreach (var path in new[] { DatabasePath, DatabasePath + "-wal" })
        {
            try
            {
                var written = File.GetLastWriteTimeUtc(path);
                if (written > latest)
                {
                    latest = written;
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        if (latest <= _lastDbWrite)
        {
            return false;
        }

        _lastDbWrite = latest;
        return true;
    }

    private void Check()
    {
        // null = the database couldn't be read just now (busy, mid-checkpoint): try again on the next change. Taking it as
        // "no notifications" on the first read set the start to 0, and the 20 oldest stored toasts then flashed by (review 10-03).
        if (Read(_lastId < 0 ? long.MaxValue : _lastId) is not { } rows)
        {
            return;
        }

        if (_lastId < 0)
        {
            // At first, only remember the latest ID so far (so old notifications don't flood in right after enabling)
            _lastId = rows.Count > 0 ? rows.Max(r => r.Id) : 0;
            return;
        }

        // Do Not Disturb (also on by itself while presenting or duplicating the screen) or presentation mode: Windows shows no
        // banner then, and neither does the bar — a private message used to appear on a shared screen (review 10-03).
        // The notifications stay in Windows' notification centre.
        var quiet = Interop.NativeMethods.DoNotDisturbOn()
                    || (Interop.NativeMethods.SHQueryUserNotificationState(out var state) == 0
                        && state is Interop.NativeMethods.QUNS_PRESENTATION_MODE or Interop.NativeMethods.QUNS_QUIET_TIME
                            or Interop.NativeMethods.QUNS_BUSY or Interop.NativeMethods.QUNS_RUNNING_D3D_FULL_SCREEN);   // a full-screen game or video: the card popped over it (review 10-03)
        foreach (var row in rows.OrderBy(r => r.Id))
        {
            _lastId = Math.Max(_lastId, row.Id);
            if (quiet)
            {
                continue;
            }

            var texts = ToastText.Texts(row.Payload);
            if (texts.Count == 0)
            {
                continue;
            }

            var now = DateTimeOffset.UtcNow;
            Board.Flash(
                "toast:" + row.Id.ToString(System.Globalization.CultureInfo.InvariantCulture),   // one per toast: a shared key kept only the last of a burst (review 10-03)
                // Content first: the notice's own title (sender, subject) in bold, then the body and the app name dim —
                // leading with the app name meant only "Windows PowerShell" showed before the notice was gone (user feedback 09-30)
                Expandable(SystemNotice.Make(texts[0], "", now, msg: ToastText.Line([.. texts.Skip(1)]), open: ToastText.LaunchProtocol(row.Payload) ?? AppLaunch(row.AppId)), AppName(row.AppId), row.AppId),
                Show,
                now);
        }
    }

    private readonly record struct Row(long Id, string AppId, string Payload);

    /// <summary>Marks a message notice for the expanded card (the island grows it out of the pill); Name carries the app, AppId the AUMID (for the app icon).</summary>
    private static IsleBar.Core.Island.ActivityState Expandable(IsleBar.Core.Island.ActivityState notice, string app, string appId)
    {
        notice.Stage = "expand";
        notice.Name = app;
        notice.AppId = appId;
        return notice;
    }

    /// <summary>
    /// Toasts with IDs greater than <paramref name="after"/>. If after is long.MaxValue, only the last one (to learn the starting ID).
    /// Opens and closes every time — holding it open can block the notification service's WAL cleanup.
    /// </summary>
    private static List<Row>? Read(long after)
    {
        var rows = new List<Row>();
        if (Sqlite.sqlite3_open_v2(DatabasePath, out var db, Sqlite.OpenReadOnly, IntPtr.Zero) != 0)
        {
            Sqlite.sqlite3_close(db);
            return null;
        }

        try
        {
            var sql = after == long.MaxValue
                ? "select n.Id, h.PrimaryId, n.Payload from Notification n join NotificationHandler h on h.RecordId = n.HandlerId order by n.Id desc limit 1"
                : "select n.Id, h.PrimaryId, n.Payload from Notification n join NotificationHandler h on h.RecordId = n.HandlerId where n.Type = 'toast' and n.Id > ?1 order by n.Id limit 20";
            if (Sqlite.sqlite3_prepare16_v2(db, sql, -1, out var statement, IntPtr.Zero) != 0)
            {
                return null;
            }

            try
            {
                if (after != long.MaxValue)
                {
                    Sqlite.sqlite3_bind_int64(statement, 1, after);
                }

                int step;
                while ((step = Sqlite.sqlite3_step(statement)) == Sqlite.Row)
                {
                    rows.Add(new Row(Sqlite.sqlite3_column_int64(statement, 0), Sqlite.Text(statement, 1), Sqlite.Blob(statement, 2)));
                }

                if (step != 101)   // SQLITE_DONE — anything else (busy, locked) stopped the read part-way
                {
                    return null;
                }
            }
            finally
            {
                Sqlite.sqlite3_finalize(statement);
            }
        }
        finally
        {
            Sqlite.sqlite3_close(db);
        }

        return rows;
    }

    /// <summary>App name: the name shown in the Start menu (shell) → otherwise guessed from the app ID. Cached once resolved.</summary>
    private string AppName(string appId)
    {
        if (_names.TryGetValue(appId, out var cached))
        {
            return cached;
        }

        var name = ShellName(appId) ?? ToastText.GuessAppName(appId);
        _names[appId] = name;
        return name;
    }

    /// <summary>How to launch the app when the toast has no deep link: <c>shell:AppsFolder\{AUMID}</c> if the shell can
    /// resolve it (opens the app), otherwise the action center. Opens the app to wherever it was — not necessarily the chat.</summary>
    private static string AppLaunch(string appId)
        => Resolvable(appId) ? @"shell:AppsFolder\" + appId : OpenNotificationCentre;   // "ms-actioncenter:" did nothing on Windows 11

    /// <summary>The card's open value that means "open Windows' notification centre" (Win+N).</summary>
    public const string OpenNotificationCentre = "islebar:notifications";

    private static bool Resolvable(string appId)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, ref iid, out var item) != 0 || item is null)
            {
                return false;
            }

            Marshal.ReleaseComObject(item);
            return true;
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            return false;
        }
    }

    private static string? ShellName(string appId)
    {
        try
        {
            var iid = typeof(IShellItem).GUID;
            if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + appId, IntPtr.Zero, ref iid, out var item) != 0 || item is null)
            {
                return null;
            }

            try
            {
                item.GetDisplayName(0 /* SIGDN_NORMALDISPLAY */, out var pointer);
                var name = Marshal.PtrToStringUni(pointer);
                Marshal.FreeCoTaskMem(pointer);
                return string.IsNullOrWhiteSpace(name) || name.Contains('!', StringComparison.Ordinal) ? null : name;
            }
            finally
            {
                Marshal.ReleaseComObject(item);
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            return null;
        }
    }

    protected override void Stop()
    {
        _stop.Cancel();
        // dispose only once the thread is out: a thread still busy (a slow first read) touched the disposed token on its next
        // round and the unhandled ObjectDisposedException ended the app (review 10-03)
        if (_thread is null || _thread.Join(TimeSpan.FromSeconds(2)))
        {
            _stop.Dispose();
        }
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItem? item);

    [ComImport]
    [Guid("43826d1e-e718-42ee-bc55-a1e261c37bfe")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr bindContext, ref Guid handler, ref Guid iid, out IntPtr result);

        void GetParent(out IShellItem parent);

        void GetDisplayName(uint form, out IntPtr name);

        void GetAttributes(uint mask, out uint attributes);

        void Compare(IShellItem other, uint hint, out int order);
    }

    /// <summary>The SQLite that ships with Windows (winsqlite3.dll) — nothing extra to install.</summary>
    private static class Sqlite
    {
        public const int OpenReadOnly = 0x00000001;
        public const int Row = 100;
        private const string Dll = "winsqlite3.dll";

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_open_v2([MarshalAs(UnmanagedType.LPUTF8Str)] string path, out IntPtr db, int flags, IntPtr vfs);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_close(IntPtr db);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall, CharSet = CharSet.Unicode)]
        public static extern int sqlite3_prepare16_v2(IntPtr db, string sql, int bytes, out IntPtr statement, IntPtr tail);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_bind_int64(IntPtr statement, int index, long value);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_step(IntPtr statement);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern int sqlite3_finalize(IntPtr statement);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        public static extern long sqlite3_column_int64(IntPtr statement, int column);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr sqlite3_column_text16(IntPtr statement, int column);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern IntPtr sqlite3_column_blob(IntPtr statement, int column);

        [DllImport(Dll, CallingConvention = CallingConvention.StdCall)]
        private static extern int sqlite3_column_bytes(IntPtr statement, int column);

        public static string Text(IntPtr statement, int column)
            => Marshal.PtrToStringUni(sqlite3_column_text16(statement, column)) ?? string.Empty;

        /// <summary>Notification content is stored as a BLOB (UTF-8 XML).</summary>
        public static string Blob(IntPtr statement, int column)
        {
            var pointer = sqlite3_column_blob(statement, column);
            var length = sqlite3_column_bytes(statement, column);
            if (pointer == IntPtr.Zero || length <= 0)
            {
                return string.Empty;
            }

            var bytes = new byte[length];
            Marshal.Copy(pointer, bytes, 0, length);
            return System.Text.Encoding.UTF8.GetString(bytes);
        }
    }
}
