using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using IsleBar.Core.SystemWatch;
using Microsoft.Win32;
using Microsoft.Win32.SafeHandles;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Microphone/camera in-use indicator. <b>Not a regular notice</b> — exposes <see cref="MicInUse"/>, <see cref="CameraInUse"/>, <see cref="AppName"/>
/// so the island can keep drawing e.g. a small dot, and adds a brief notice (2 s) "Microphone in use · Zoom" only at the moment use starts.
/// Anything already in use at startup isn't a "start", so it isn't notified.
/// <para>
/// Source: <c>HKCU\…\CapabilityAccessManager\ConsentStore\{microphone,webcam}</c> — the same values as the taskbar's microphone icon.
/// Store apps are keys directly below, regular apps are keys under <c>NonPackaged</c>, and in use if <c>LastUsedTimeStop</c> is 0
/// (rules in <see cref="PrivacyRules"/>). Waits for registry change notifications (RegNotifyChangeKeyValue, including subkeys) but
/// also reads directly every 2 s (in case notifications are missed). The island itself (IsleBar) is excluded.
/// </para>
/// Runs on a dedicated thread — no UI thread needed. Properties are safe to read from any thread.
/// </summary>
internal sealed class PrivacyWatcher : NoticeWatcher
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";
    private const string Microphone = "microphone";
    private const string Webcam = "webcam";
    private const string NonPackaged = "NonPackaged";
    private const string PrivacyUri = "ms-settings:privacy";
    private static readonly TimeSpan Show = TimeSpan.FromSeconds(2);
    private const int PollMs = 2000;

    private const uint RegNotifyChangeName = 0x1;
    private const uint RegNotifyChangeLastSet = 0x4;

    private static readonly ConcurrentDictionary<string, string> NameCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ManualResetEvent _stop = new(false);
    private readonly string? _ownExe = Environment.ProcessPath;
    private Thread? _thread;
    private bool _first = true;
    private volatile State _state = new(null, null);

    public PrivacyWatcher(Func<Core.Localization.LanguageStrings> strings)
        : base(strings)
    {
    }

    /// <summary>Whether any app is using the microphone now.</summary>
    public bool MicInUse => _state.Mic is not null;

    /// <summary>Whether any app is using the camera now.</summary>
    public bool CameraInUse => _state.Camera is not null;

    /// <summary>Name of the app using it (camera takes priority — in a video call both are the same app). Null if none.</summary>
    public string? AppName => _state.Camera ?? _state.Mic;

    public override void Start() => Guard(() =>
    {
        if (_thread is not null)
        {
            return;
        }

        _thread = new Thread(Loop) { IsBackground = true, Name = "IsleBar privacy" };
        _thread.Start();
    });

    protected override void Stop()
    {
        _stop.Set();
        _thread?.Join(TimeSpan.FromSeconds(3));
        _stop.Dispose();
    }

    private void Loop()
    {
        using var changed = new AutoResetEvent(false);
        RegistryKey? root = null;
        var armed = false;
        try
        {
            while (!IsDisposed)
            {
                try
                {
                    root ??= Registry.CurrentUser.OpenSubKey(ConsentStore);
                    if (root is not null && !armed)
                    {
                        // The async notification fires only "once", so re-arm it — but only after it fired: arming again on every
                        // 2-second timeout queued another request in the kernel each time (review 10-03). The arming thread must
                        // stay alive, hence a dedicated thread.
                        armed = RegNotifyChangeKeyValue(root.Handle, true, RegNotifyChangeName | RegNotifyChangeLastSet,
                            changed.SafeWaitHandle, true) == 0;
                    }
                }
                catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException or ObjectDisposedException)
                {
                    root?.Dispose();
                    root = null;
                    armed = false;
                }

                Guard(Scan);

                var hit = WaitHandle.WaitAny([changed, _stop], PollMs);
                if (hit == 1)
                {
                    return;
                }

                if (hit == 0)
                {
                    armed = false;   // it fired — arm again next round
                }
            }
        }
        catch (ObjectDisposedException)
        {
            // shutting down
        }
        finally
        {
            root?.Dispose();
        }
    }

    private void Scan()
    {
        var mic = FindUser(Microphone);
        var camera = FindUser(Webcam);
        var before = _state;
        _state = new State(mic, camera);

        if (_first)
        {
            _first = false;   // don't notify what was already in use at startup
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var s = Text;
        if (camera is not null && before.Camera is null)
        {
            Board.Flash(Webcam, SystemNotice.Make(NoticeText.InUse(s, camera: true, camera), NoticeGlyphs.Camera, now, open: PrivacyUri + "-webcam"), Show, now);
        }

        if (mic is not null && before.Mic is null)
        {
            Board.Flash(Microphone, SystemNotice.Make(NoticeText.InUse(s, camera: false, mic), NoticeGlyphs.Microphone, now, open: PrivacyUri + "-microphone"), Show, now);
        }
    }

    /// <summary>Name of the app that most recently started using that device. Null if none.</summary>
    private string? FindUser(string capability)
    {
        using var key = Registry.CurrentUser.OpenSubKey(ConsentStore + "\\" + capability);
        if (key is null)
        {
            return null;
        }

        string? best = null;
        long bestStart = 0;

        void Consider(RegistryKey parent, string child, bool packaged)
        {
            using var app = parent.OpenSubKey(child);
            if (app is null)
            {
                return;
            }

            var start = app.GetValue("LastUsedTimeStart") as long?;
            var stop = app.GetValue("LastUsedTimeStop") as long?;
            if (!PrivacyRules.IsInUse(start, stop) || start!.Value < bestStart)
            {
                return;
            }

            if (!packaged && PrivacyRules.IsSelf(child, _ownExe))
            {
                return;
            }

            bestStart = start.Value;
            best = packaged ? PrivacyRules.FriendlyName(child, packaged: true) : NonPackagedName(child);
        }

        foreach (var child in key.GetSubKeyNames())
        {
            if (child.Equals(NonPackaged, StringComparison.OrdinalIgnoreCase))
            {
                using var nonPackaged = key.OpenSubKey(child);
                if (nonPackaged is not null)
                {
                    foreach (var exe in nonPackaged.GetSubKeyNames())
                    {
                        Consider(nonPackaged, exe, packaged: false);
                    }
                }

                continue;
            }

            Consider(key, child, packaged: true);
        }

        return best;
    }

    /// <summary>Regular app name: the exe's "File description" (e.g. "Google Chrome") if present, else the exe name. Cached once resolved.</summary>
    private static string NonPackagedName(string keyName)
        => NameCache.GetOrAdd(keyName, static k =>
        {
            var fallback = PrivacyRules.FriendlyName(k, packaged: false);
            try
            {
                var path = PrivacyRules.PathFromNonPackagedKey(k);
                if (File.Exists(path)
                    && FileVersionInfo.GetVersionInfo(path).FileDescription is { } description
                    && !string.IsNullOrWhiteSpace(description)
                    && description.Trim().Length <= 32)
                {
                    return description.Trim();
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
            }

            return fallback;
        });

    private sealed record State(string? Mic, string? Camera);

    [DllImport("advapi32.dll")]
    private static extern int RegNotifyChangeKeyValue(
        SafeRegistryHandle key, bool watchSubtree, uint notifyFilter, SafeWaitHandle eventHandle, bool asynchronous);
}
