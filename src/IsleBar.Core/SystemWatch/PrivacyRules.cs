namespace IsleBar.Core.SystemWatch;

/// <summary>
/// Rules for reading microphone/camera usage records (registry <c>CapabilityAccessManager\ConsentStore</c>).
/// When an app starts using a device Windows writes <c>LastUsedTimeStart</c>, and <c>LastUsedTimeStop</c> when it lets go —
/// while in use, Stop is 0. The taskbar's microphone icon looks at the same values.
/// App key names: Store apps = package family name (<c>Microsoft.WindowsCamera_8wekyb3d8bbwe</c>),
/// regular apps (under <c>NonPackaged</c>) = the full exe path with <c>\</c> replaced by <c>#</c>.
/// </summary>
public static class PrivacyRules
{
    /// <summary>Whether it is in use: there is a start record and the stop record is 0.</summary>
    public static bool IsInUse(long? lastUsedStart, long? lastUsedStop)
        => lastUsedStart is > 0 && lastUsedStop is 0;

    /// <summary>Regular app key name → exe path. <c>C:#Program Files#Zoom#bin#Zoom.exe</c> → <c>C:\Program Files\Zoom\bin\Zoom.exe</c>.</summary>
    public static string PathFromNonPackagedKey(string keyName)
        => (keyName ?? string.Empty).Replace('#', '\\');

    /// <summary>
    /// A human-readable name (fallback when the file description cannot be read).
    /// Regular apps use the exe name (without extension), Store apps the part of the package name after the last dot (<c>Microsoft.WindowsCamera_…</c> → <c>WindowsCamera</c>).
    /// </summary>
    public static string FriendlyName(string keyName, bool packaged)
    {
        if (string.IsNullOrWhiteSpace(keyName))
        {
            return string.Empty;
        }

        if (!packaged)
        {
            var path = PathFromNonPackagedKey(keyName);
            var cut = path.LastIndexOfAny(['\\', '/']);
            var file = cut >= 0 ? path[(cut + 1)..] : path;
            return file.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? file[..^4] : file;
        }

        var family = keyName;
        var underscore = family.IndexOf('_', StringComparison.Ordinal);
        if (underscore > 0)
        {
            family = family[..underscore];
        }

        var dot = family.LastIndexOf('.');
        return dot >= 0 && dot < family.Length - 1 ? family[(dot + 1)..] : family;
    }

    /// <summary>Whether this regular app key is our own program (the island does not report itself).</summary>
    public static bool IsSelf(string keyName, string? ownExePath)
        => !string.IsNullOrEmpty(ownExePath)
           && string.Equals(PathFromNonPackagedKey(keyName), ownExePath, StringComparison.OrdinalIgnoreCase);
}
