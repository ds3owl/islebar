namespace IsleBar.Core.Island;

/// <summary>
/// Classifies the playback source shown on the music island. <b>Browser playback (YouTube etc.) is excluded</b> — music apps only
/// (user feedback 09-30: internet playback was showing up too; apply it to music apps only).
/// Windows reports the source as an app ID (e.g. <c>chrome.exe</c>, <c>Chrome</c>, <c>MSEdge</c>,
/// <c>SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify</c>). Music apps are too varied to allow-list,
/// so browsers are filtered by a list instead.
/// </summary>
public static class MediaSources
{
    private static readonly string[] BrowserNames =
    [
        "chrome", "msedge", "edge", "firefox", "opera", "brave", "whale", "vivaldi", "iexplore", "arc", "chromium", "waterfox",
    ];

    /// <summary>Whether the playback comes from a browser.</summary>
    public static bool IsBrowser(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return false;
        }

        // accepts all of "C:\...\chrome.exe" · "chrome.exe" · "Chrome" · "Microsoft.MicrosoftEdge_8wekyb3d8bbwe!MicrosoftEdge"
        var name = appId.Trim();
        var cut = name.LastIndexOfAny(['\\', '/', '!']);
        if (cut >= 0)
        {
            name = name[(cut + 1)..];
        }

        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            name = name[..^4];
        }

        name = name.ToLowerInvariant();
        // short names (arc, edge) only on exact match — so unrelated apps like "archive.exe" are not mistaken for browsers
        return name.Contains("microsoftedge", StringComparison.Ordinal)
               || BrowserNames.Any(b => name == b || (b.Length >= 5 && name.StartsWith(b, StringComparison.Ordinal)));
    }
}
