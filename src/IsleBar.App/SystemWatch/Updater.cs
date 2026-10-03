using System.Security.Cryptography;
using IsleBar.Core.Island;
using IsleBar.Core.Localization;
using IsleBar.Core.Updates;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// One-click update (10-01): downloads the new setup from the GitHub release, refuses it unless its SHA-256 matches the
/// checksum file published with it, then runs it silently. Setup stops the bar, installs over it (per-user, no admin
/// prompt) and starts it again. The download shows on the pill like any other transfer.
/// </summary>
internal static class Updater
{
    /// <summary>The update card's "open" value that means "install it" rather than "open a page".</summary>
    public const string Command = "islebar:update";

    private const string StateId = "islebar_update";
    private static readonly HttpClient Http = CreateClient();
    private static int _running;

    /// <summary>Strings for the current language (set by the window; English until then).</summary>
    public static Func<LanguageStrings> Strings { get; set; } = () => LanguageCatalog.For(LanguageCatalog.Fallback);

    /// <summary>Starts the update for the release the watcher found. Does nothing if one is already running.</summary>
    public static void Start()
    {
        if (UpdateWatcher.Available is not { } release)
        {
            return;
        }

        if (release.SetupUrl is null || release.ShaUrl is null)
        {
            OpenPage(release.Page);   // an older release without a checksum: let the person download it themselves
            return;
        }

        if (Interlocked.Exchange(ref _running, 1) != 0)
        {
            return;
        }

        _ = Task.Run(() => RunAsync(release));
    }

    private static async Task RunAsync(Release release)
    {
        var store = new ActivityStore(Interop.AppPaths.StateDirectory);
        var name = $"IsleBar {release.Version.ToString(3)}";
        var t0 = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;   // one start time, so the pill can show time left
        var launched = false;
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "IsleBar", "update");
            Directory.CreateDirectory(dir);
            var setup = Path.Combine(dir, $"IsleBar-Setup-{release.Version.ToString(3)}.exe");
            foreach (var old in Directory.GetFiles(dir, "IsleBar-Setup-*.exe").Where(f => !string.Equals(f, setup, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    File.Delete(old);   // earlier updates' setups (about 70 MB each) used to stay in Temp
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            var expected = await Http.GetStringAsync(release.ShaUrl).ConfigureAwait(false);
            using (var response = await Http.GetAsync(release.SetupUrl, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false))
            {
                response.EnsureSuccessStatusCode();
                var total = response.Content.Headers.ContentLength;
                await using var from = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
                await using var to = File.Create(setup);
                var buffer = new byte[81920];
                long done = 0;
                var lastShown = DateTime.MinValue;
                int read;
                // a connection that goes quiet mid-download gives up after 30 s without a byte (it waited forever and the
                // update stayed locked until a restart — review 10-03)
                using var quiet = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                while ((read = await from.ReadAsync(buffer, quiet.Token).ConfigureAwait(false)) > 0)
                {
                    quiet.CancelAfter(TimeSpan.FromSeconds(30));
                    await to.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
                    done += read;
                    if (DateTime.UtcNow - lastShown > TimeSpan.FromMilliseconds(250))
                    {
                        lastShown = DateTime.UtcNow;
                        Show(store, name, done, total, "run", t0);
                    }
                }
            }

            byte[] hash;
            await using (var file = File.OpenRead(setup))
            {
                hash = await SHA256.HashDataAsync(file).ConfigureAwait(false);
            }

            if (!ReleaseInfo.HashMatches(expected, hash))
            {
                File.Delete(setup);
                throw new InvalidDataException("checksum mismatch");
            }

            Show(store, name, 1, 1, "done", t0);
            Interop.AppLog.Write($"update: installing {name}");
            // setup stops this bar (and its supervisor), installs over it and starts it again (/RELAUNCH=1 — see IsleBar.iss)
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(setup)
            {
                Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /RELAUNCH=1",
                UseShellExecute = false,
            });
            launched = true;
        }
        catch (Exception ex)   // anything at all: a failure must never leave the card up or the update locked until the next start
        {
            Interop.AppLog.Write("update failed: " + ex.Message);
            store.Remove(StateId);
            var notice = Core.SystemWatch.SystemNotice.Make(name, Core.SystemWatch.NoticeGlyphs.Download, DateTimeOffset.UtcNow,
                msg: Strings().UpdateFailed, open: release.Page);
            notice.Stage = "expand";
            notice.Name = "IsleBar";
            store.Write(StateId, notice);
        }
        finally
        {
            if (launched)
            {
                // setup normally stops this process within seconds; if it was blocked or cancelled instead, allow another try
                await Task.Delay(TimeSpan.FromMinutes(2)).ConfigureAwait(false);
            }

            Interlocked.Exchange(ref _running, 0);
        }
    }

    private static void Show(ActivityStore store, string name, long done, long? total, string state, double t0)
        => store.Write(StateId, new ActivityState
        {
            RawKind = ActivityState.KindTransfer,
            Title = "📥 GitHub → PC",   // a title ending in "PC" draws the incoming arrow
            Name = name,
            Done = done,
            Total = total,
            State = state,
            T0 = t0,
        });

    public static void OpenPage(string url)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // no browser registered — nothing more to do
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"IsleBar/{UpdateWatcher.RunningVersion.ToString(3)}");
        return http;
    }
}
