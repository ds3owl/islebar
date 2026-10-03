using System.Text.Json;

namespace IsleBar.Core.Updates;

/// <summary>
/// Reads GitHub's "latest release" answer and decides whether it is newer than the running build. The check sends nothing
/// about the person — it is the same public request a browser makes when opening the releases page (10-01).
/// </summary>
public static class ReleaseInfo
{
    public const string LatestApi = "https://api.github.com/repos/ds3owl/islebar/releases/latest";
    public const string ReleasesPage = "https://github.com/ds3owl/islebar/releases/latest";

    /// <summary>"v0.2.0" / "0.2" / "IsleBar 1.3.4" → a version; null if there is none.</summary>
    public static Version? ParseTag(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag))
        {
            return null;
        }

        var start = tag.IndexOfAny("0123456789".ToCharArray());
        if (start < 0)
        {
            return null;
        }

        var end = start;
        while (end < tag.Length && (char.IsDigit(tag[end]) || tag[end] == '.'))
        {
            end++;
        }

        var text = tag[start..end].TrimEnd('.');
        if (!text.Contains('.', StringComparison.Ordinal))
        {
            text += ".0";
        }

        return Version.TryParse(text, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;   // always major.minor.build
    }

    /// <summary>
    /// The newest published (non-draft, non-prerelease) release in GitHub's JSON: its version, its page, and — when attached —
    /// the setup exe and its SHA-256 file (what one-click update needs). Null if unreadable.
    /// </summary>
    public static Release? ParseLatest(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || (root.TryGetProperty("draft", out var d) && d.ValueKind == JsonValueKind.True)
                || (root.TryGetProperty("prerelease", out var p) && p.ValueKind == JsonValueKind.True))
            {
                return null;
            }

            var version = ParseTag(root.TryGetProperty("tag_name", out var tag) ? tag.GetString() : null);
            if (version is null)
            {
                return null;
            }

            var page = root.TryGetProperty("html_url", out var u) && u.GetString() is { } s && s.StartsWith(RepoPrefix, StringComparison.OrdinalIgnoreCase) ? s : ReleasesPage;
            var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var name = asset.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                    var url = asset.TryGetProperty("browser_download_url", out var b) ? b.GetString() : null;
                    if (url is not null && name.Length > 0 && url.StartsWith(DownloadPrefix, StringComparison.OrdinalIgnoreCase))
                    {
                        files[name] = url;   // only IsleBar's own release downloads
                    }
                }
            }

            // the setup and the checksum that belongs to *that* file (name + ".sha256") — with several setups attached, a checksum
            // picked separately could belong to another one and every update would fail (code review 10-02)
            string? setup = null, sha = null;
            foreach (var (name, url) in files)
            {
                if (name.StartsWith("IsleBar-Setup-", StringComparison.OrdinalIgnoreCase) && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    && files.TryGetValue(name + ".sha256", out var hashUrl))
                {
                    setup = url;
                    sha = hashUrl;
                    break;
                }
            }

            setup ??= files.FirstOrDefault(f => f.Key.StartsWith("IsleBar-Setup-", StringComparison.OrdinalIgnoreCase) && f.Key.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)).Value;

            return new Release(version, page, setup, sha);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a ".sha256" file ("&lt;64 hex&gt;" optionally followed by the file name) and compares it with the hash of what was
    /// downloaded. Anything malformed counts as a mismatch — a download that can't be checked is never installed.
    /// </summary>
    public static bool HashMatches(string? shaFile, byte[] actualHash)
    {
        ArgumentNullException.ThrowIfNull(actualHash);
        var hex = shaFile?.Trim().Split((char[]?)null, 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        if (hex is null || hex.Length != 64 || actualHash.Length != 32)
        {
            return false;
        }

        return string.Equals(hex, Convert.ToHexString(actualHash), StringComparison.OrdinalIgnoreCase);
    }

    private const string RepoPrefix = "https://github.com/ds3owl/islebar/";
    private const string DownloadPrefix = "https://github.com/ds3owl/islebar/releases/download/";

    /// <summary>Compares major.minor.build only (0.2 equals 0.2.0).</summary>
    public static bool IsNewer(Version latest, Version current)
    {
        ArgumentNullException.ThrowIfNull(latest);
        ArgumentNullException.ThrowIfNull(current);
        static Version Norm(Version v) => new(v.Major, Math.Max(0, v.Minor), Math.Max(0, v.Build));
        return Norm(latest) > Norm(current);
    }
}

/// <summary>A published release: version, its page, and the attached setup exe and SHA-256 file (null when not attached).</summary>
public sealed record Release(Version Version, string Page, string? SetupUrl, string? ShaUrl);
