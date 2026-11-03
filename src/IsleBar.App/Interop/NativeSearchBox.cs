using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace IsleBar.App.Interop;

/// <summary>
/// Makes the real search box transparent directly inside the taskbar (user feedback 09-30: go native, even if hard, if at all possible).
/// <para>
/// The pill used to cover the real search box, so when the taskbar icon count changed and the real box moved, it showed through while we caught up
/// (still visible even with tracking cut to 50 ms, which only raised CPU). So via Windows' XAML diagnostics channel (InitializeXamlDiagnosticsEx —
/// the documented API behind Visual Studio's live visual tree) we attach a small module (native\IsleBarTap) to Explorer
/// and set only the search box's <b>opacity</b> to 0. Its slot stays, so icons don't move, and the pill sits in that empty slot.
/// </para>
/// <para>
/// Safety: the module reverts on its own when the IsleBar process ends (including a forced kill). Turning the setting off
/// makes <see cref="Show"/> signal it to revert immediately. When Explorer restarts, <see cref="Hide"/> is called again.
/// </para>
/// </summary>
internal static class NativeSearchBox
{
    private const string ShowEventName = @"Local\IsleBar.NativeSearchBox.Show";
    private const string TapPrefix = @"Local\IsleBar.Tap.";   // + "<explorer pid>." — the tap's object names (IsleBarTap.cpp)
    private const int TagChars = 32;                           // size of the tap's "Current" record in UTF-16 units, NUL included
    private static readonly Guid TapClsid = new("28318384-b9c8-4ab9-8fbe-7bbe5571f408");
    private const int MaxConnections = 32;
    private static int _injectedInto;
    private static int _attaching;   // one attach at a time (an attach can now wait for Explorer)
    private static volatile bool _wantHidden;
    private static int _requests;    // bumped by every Hide/Show request, so a worker that started earlier catches up

    /// <summary>
    /// Asks for the search box to be hidden (setting on, start, Explorer restarted). Returns at once; the work (which can wait for
    /// Explorer's XAML) runs on the thread pool. The wish is recorded here, on the caller's thread, so a Show that follows is
    /// never overtaken by this request's late start (setting off right after on left the box hidden — review 10-03).
    /// </summary>
    public static void RequestHide()
    {
        _wantHidden = true;
        Interlocked.Increment(ref _requests);
        _ = Task.Run(Sync);
    }

    /// <summary>Shows the search box again (setting turned off, or exiting) — at once, on the caller's thread.</summary>
    public static void Show()
    {
        _wantHidden = false;
        Interlocked.Increment(ref _requests);
        SignalShow();
    }

    /// <summary>Brings the module in line with the latest request; a request made while it works is picked up before it stops.</summary>
    private static void Sync()
    {
        if (Interlocked.Exchange(ref _attaching, 1) == 1)
        {
            return;   // the worker already running loops until it has caught up
        }

        int seen;
        try
        {
            do
            {
                seen = Volatile.Read(ref _requests);
                if (_wantHidden)
                {
                    HideOnce();
                }
                else
                {
                    SignalShow();
                }
            }
            while (seen != Volatile.Read(ref _requests));
        }
        finally
        {
            Volatile.Write(ref _attaching, 0);
        }

        if (seen != Volatile.Read(ref _requests))
        {
            Sync();   // a request that arrived between the last check and letting go
        }
    }

    private static void SignalShow()
    {
        using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, ShowEventName);
        ev.Set();
        _injectedInto = 0;
    }

    /// <summary>
    /// Attaches this build's module to Explorer (or wakes the one already there) to hide the search box. Does nothing if this Explorer
    /// is already done. Fails quietly (log only).
    /// </summary>
    private static void HideOnce()
    {
        try
        {
            var tray = TaskbarHost.FindTaskbar();
            if (tray == IntPtr.Zero)
            {
                return;
            }

            _ = GetWindowThreadProcessId(tray, out var explorerPid);
            // "already hidden in this Explorer" only while no Show is pending: a Show that came in while an attach was still running
            // was overwritten by the attach's late "done", and the box then stayed visible behind the pill (review 10-03)
            if (explorerPid == 0 || (explorerPid == _injectedInto && !ShowSignalled()))
            {
                return;
            }

            var staged = StageDll();
            if (staged is null)
            {
                TaskbarHost.Log("native search box: module file missing");
                return;
            }

            var (dll, tag) = staged.Value;
            ResetShowEvent();
            var prefix = $"{TapPrefix}{explorerPid}.";
            if (WakeExistingTap(prefix + tag + "."))
            {
                TaskbarHost.Log($"native search box: woke the module already in explorer={explorerPid}");
                _injectedInto = (int)explorerPid;
                return;
            }

            // A module of an earlier build is in this Explorer (IsleBar was updated while Explorer ran): wake that one rather than
            // attach a second — attaching to an Explorer that already had the module crashed it about 1 time in 10 (10-02/03). This
            // build's module goes in on the next Explorer start (sign-in, restart).
            var current = ReadCurrentTag(explorerPid);
            if ((current.Length > 0 && current != tag && WakeExistingTap(prefix + current + ".")) || WakeExistingTap(prefix))
            {
                TaskbarHost.Log($"native search box: woke an earlier module in explorer={explorerPid} (this build's goes in when Explorer restarts)");
                _injectedInto = (int)explorerPid;
                return;
            }

            // The XAML diagnostics host only takes endpoints named "VisualDiagConnection" + a number, one connection per number
            // (measured 10-02: any other name answers ERROR_NOT_FOUND). A number already in use answers the same, so take the first free one.
            const int ErrorNotFound = unchecked((int)0x80070490);
            // "<owner pid> <tag>": the module puts the tag in its object names, so only bars shipping this same module wake it later
            var owner = string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{Environment.ProcessId} {tag}");
            var hr = ErrorNotFound;
            // Right after Explorer (re)starts its XAML isn't up yet and every number answers ERROR_NOT_FOUND too; that attempt used to be
            // the only one, so native mode stayed off until IsleBar restarted (seen 10-02). Give it half a minute. (Runs off the UI thread.)
            for (var attempt = 0; hr == ErrorNotFound && attempt < 6; attempt++)
            {
                if (attempt > 0)
                {
                    Thread.Sleep(TimeSpan.FromSeconds(5));
                }

                for (var n = 1; hr == ErrorNotFound && n <= MaxConnections; n++)
                {
                    hr = InitializeXamlDiagnosticsEx("VisualDiagConnection" + n, explorerPid, string.Empty, dll, TapClsid, owner);
                }
            }

            TaskbarHost.Log($"native search box: attach hr=0x{hr:X8} explorer={explorerPid}");
            if (hr >= 0)
            {
                _injectedInto = (int)explorerPid;
            }
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or IOException or UnauthorizedAccessException or COMException)
        {
            TaskbarHost.Log("native search box failed: " + ex.Message);
        }
    }

    /// <summary>
    /// The module stays in Explorer for Explorer's whole life (attaching to the same Explorer again crashed it about 1 time in 10 —
    /// 8 times on 10-02/03 — while the first attach after Explorer starts never did). A later IsleBar run writes its process id into
    /// the module's shared memory and wakes it, and the module hides the box again while that process lives. <paramref name="prefix"/>
    /// names one module: "Local\IsleBar.Tap.&lt;explorer pid&gt;." plus its tag (content hash) and a dot, or no tag for a module from
    /// before tags. False when this Explorer has no such module.
    /// </summary>
    private static bool WakeExistingTap(string prefix)
    {
        try
        {
            using var wake = EventWaitHandle.OpenExisting(prefix + "Wake");
            using (var owner = MemoryMappedFile.OpenExisting(prefix + "Owner"))
            using (var view = owner.CreateViewAccessor(0, sizeof(uint)))
            {
                view.Write(0, (uint)Environment.ProcessId);
                view.Flush();
            }

            wake.Set();
            return true;
        }
        catch (Exception ex) when (ex is WaitHandleCannotBeOpenedException or FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>The tag of the module that took the latest owner in this Explorer ("" when none with tags is there).</summary>
    private static string ReadCurrentTag(uint explorerPid)
    {
        try
        {
            using var current = MemoryMappedFile.OpenExisting($"{TapPrefix}{explorerPid}.Current", MemoryMappedFileRights.Read);
            using var view = current.CreateViewAccessor(0, TagChars * sizeof(char), MemoryMappedFileAccess.Read);
            var chars = new char[TagChars];
            view.ReadArray(0, chars, 0, TagChars);
            var end = Array.IndexOf(chars, '\0');
            var tag = new string(chars, 0, end < 0 ? TagChars : end);
            return tag.All(char.IsAsciiLetterOrDigit) ? tag : string.Empty;   // it becomes part of object names
        }
        catch (Exception ex) when (ex is FileNotFoundException or IOException or UnauthorizedAccessException)
        {
            return string.Empty;
        }
    }

    private static bool ShowSignalled()
    {
        using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, ShowEventName);
        return ev.WaitOne(0);
    }

    private static void ResetShowEvent()
    {
        using var ev = new EventWaitHandle(false, EventResetMode.ManualReset, ShowEventName);
        ev.Reset();
    }

    /// <summary>
    /// Copies the module to where Explorer will load it. Explorer never releases a file it has loaded, so when the content changes we copy under a new name (content hash).
    /// The hash is also the module's tag (its object names), so a module of another build is never woken in its place.
    /// </summary>
    private static (string Path, string Tag)? StageDll()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "native", "IsleBarTap.dll");
        if (!File.Exists(source))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(source);
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12];
        // Store build: AppData writes of a packaged app land in its private copy, which Explorer can't see — keep the module in
        // a plain folder of the user profile instead (the reason the Store refused unvirtualizedResources, 10-07).
        var folder = AppPaths.IsPackaged
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".islebar", "tap")
            : Path.Combine(AppPaths.DataDirectory, "tap");
        Directory.CreateDirectory(folder);
        var target = Path.Combine(folder, $"IsleBarTap-{hash}.dll");
        if (!File.Exists(target))
        {
            File.WriteAllBytes(target, bytes);
        }

        // Copies of earlier builds pile up here. One still loaded in an Explorer can't be deleted (Windows locks a loaded module's
        // file), so it's skipped and goes on a run after that Explorer has restarted.
        foreach (var old in Directory.GetFiles(folder, "IsleBarTap*.dll"))
        {
            if (string.Equals(Path.GetFileName(old), Path.GetFileName(target), StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                File.Delete(old);
                TaskbarHost.Log("native search box: removed the old module copy " + Path.GetFileName(old));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        return (target, hash);
    }

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

    [DllImport("Windows.UI.Xaml.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int InitializeXamlDiagnosticsEx(string endPointName, uint pid, string dllXamlDiagnostics, string tapDllName, Guid tapClsid, string initializationData);
}
