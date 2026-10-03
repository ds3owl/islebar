using System.Xml.Linq;

namespace IsleBar.Core.SystemWatch;

/// <summary>
/// Extracts the text to show on the island from one Windows notification (toast). Notifications are kept as XML in the Windows notification database (wpndatabase.db)
/// (the only way to read them without app signing/packaging — the official notification listener API needs a signed package, 09-30).
/// </summary>
public static class ToastText
{
    /// <summary>The &lt;text&gt; elements of the notification XML (empty ones dropped, whitespace trimmed). Empty list if unreadable.</summary>
    public static IReadOnlyList<string> Texts(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return [];
        }

        try
        {
            var doc = XDocument.Parse(payload);
            return [.. doc.Descendants().Where(e => e.Name.LocalName == "text")
                .Select(e => string.Join(' ', e.Value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)))
                .Where(t => t.Length > 0)];
        }
        catch (System.Xml.XmlException)
        {
            return [];
        }
    }

    /// <summary>
    /// Guesses a human-readable name from the app ID (AUMID) (only used when the Start menu name could not be found).
    /// "SAMSUNGELECTRONICSCO.LTD.SamsungSettings1.5_3c1yjt4zspk6g!App" → "SamsungSettings",
    /// "Windows.Defender.SecurityCenter" → "SecurityCenter", "Phone → PC" → unchanged.
    /// </summary>
    public static string GuessAppName(string? appId)
    {
        if (string.IsNullOrWhiteSpace(appId))
        {
            return "Windows";
        }

        var name = appId;
        var bang = name.IndexOf('!', StringComparison.Ordinal);
        if (bang >= 0)
        {
            name = name[..bang];
        }

        var underscore = name.IndexOf('_', StringComparison.Ordinal);
        if (underscore > 0)
        {
            name = name[..underscore];
        }

        if (name.Contains('\\', StringComparison.Ordinal))
        {
            name = Path.GetFileNameWithoutExtension(name);   // for desktop apps the ID is sometimes the exe path
        }

        var parts = name.Split('.', StringSplitOptions.RemoveEmptyEntries);
        var last = parts.LastOrDefault(p => !p.All(char.IsDigit)) ?? name;
        // strip a trailing version number, as in "SamsungSettings1"
        last = last.TrimEnd('0', '1', '2', '3', '4', '5', '6', '7', '8', '9');
        return last.Length > 0 ? last : name;
    }

    /// <summary>
    /// The deep link to open when the notification is tapped, if the toast uses <c>activationType="protocol"</c> — then its
    /// <c>launch</c> is a URI (e.g. <c>whatsapp://…</c>, <c>tg://…</c>) that opens the exact chat, so tapping the card can too.
    /// <para>
    /// Returns <see langword="null"/> for the common case where <c>launch</c> is opaque app data only the app's own activator
    /// can consume (activationType foreground/background): the caller then falls back to just launching the app. So the chat
    /// opens for apps that publish a protocol link, and the app opens for the rest — best effort, app by app.
    /// </para>
    /// </summary>
    public static string? LaunchProtocol(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            return null;
        }

        try
        {
            var toast = XDocument.Parse(payload).Descendants().FirstOrDefault(e => e.Name.LocalName == "toast");
            var launch = toast?.Attribute("launch")?.Value;
            var activation = toast?.Attribute("activationType")?.Value;
            if (string.IsNullOrWhiteSpace(launch) || !string.Equals(activation, "protocol", StringComparison.OrdinalIgnoreCase))
            {
                return null;
            }

            // Only real URI schemes are safe to hand to ShellExecute; a bare path or opaque token is not a deep link.
            return Uri.TryCreate(launch, UriKind.Absolute, out var uri) && !uri.IsFile ? uri.AbsoluteUri : null;
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    /// <summary>The single line shown on the island: joins multiple texts with " · " and cuts it if too long.</summary>
    public static string Line(IReadOnlyList<string> texts, int max = 120)
    {
        var line = string.Join(" · ", texts);
        return line.Length <= max ? line : line[..max].TrimEnd() + "…";
    }
}
