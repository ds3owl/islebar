using IsleBar.Core.Localization;
using IsleBar.Core.SystemWatch;
using IsleBar.Core.Updates;

namespace IsleBar.App.SystemWatch;

/// <summary>
/// Once a day (first 30 s after start) asks GitHub for the latest IsleBar release. When a newer one is out it shows a card on
/// the bar once per version — clicking it opens the download page — and the right-click menu offers it until installed.
/// Sends nothing about the person: it is the same public request a browser makes for the releases page (10-01).
/// </summary>
internal sealed class UpdateWatcher(Func<LanguageStrings> strings) : NoticeWatcher(strings)
{
    /// <summary>This build's version. Declared first: static fields start in order, and the HTTP client below uses it.</summary>
    public static Version RunningVersion { get; } = typeof(UpdateWatcher).Assembly.GetName().Version ?? new Version(0, 0, 0);

    private static readonly HttpClient Http = CreateClient();
    private static readonly string NotifiedFile = Path.Combine(Interop.AppPaths.DataDirectory, "update_notified.txt");
    private Timer? _timer;

    /// <summary>A newer release, once found (shared with the right-click menu and the updater). Null until then.</summary>
    public static Release? Available { get; private set; }


    public override void Start()
        => _timer ??= new Timer(_ => Guard(Check), null, TimeSpan.FromSeconds(30), TimeSpan.FromHours(24));

    private void Check()
    {
        string json;
        try
        {
            json = Http.GetStringAsync(ReleaseInfo.LatestApi).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            // offline (a laptop on Wi-Fi a little after sign-in) or no release yet (404) — look again in an hour, not tomorrow (review 10-03)
            _timer?.Change(TimeSpan.FromHours(1), TimeSpan.FromHours(24));
            return;
        }

        if (ReleaseInfo.ParseLatest(json) is not { } latest || !ReleaseInfo.IsNewer(latest.Version, RunningVersion))
        {
            return;
        }

        Available = latest;
        var version = latest.Version.ToString(3);
        if (ReadNotified() == version)
        {
            return;   // shown once already; the menu keeps offering it
        }

        WriteNotified(version);
        var now = DateTimeOffset.UtcNow;
        // clicking the card installs it (one-click update) — or opens the page for a release without a checksum
        var open = latest.SetupUrl is not null && latest.ShaUrl is not null ? Updater.Command : latest.Page;
        var notice = SystemNotice.Make($"IsleBar {version}", NoticeGlyphs.Download, now, msg: Text.UpdateAvailable, open: open);
        notice.Stage = "expand";   // the card that grows out of the pill
        notice.Name = "IsleBar";
        Board.Flash("update", notice, TimeSpan.FromSeconds(15), now);
    }

    private static string? ReadNotified()
    {
        try
        {
            return File.Exists(NotifiedFile) ? File.ReadAllText(NotifiedFile).Trim() : null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static void WriteNotified(string version)
    {
        try
        {
            File.WriteAllText(NotifiedFile, version);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // worst case the card shows again tomorrow
        }
    }

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd($"IsleBar/{RunningVersion.ToString(3)}");   // GitHub's API requires a User-Agent
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return http;
    }

    protected override void Stop() => _timer?.Dispose();
}
