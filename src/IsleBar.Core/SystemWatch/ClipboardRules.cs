using System.Text;

namespace IsleBar.Core.SystemWatch;

/// <summary>
/// Trimming the text shown in copy notices, and <b>masking passwords</b>.
/// The island sits on the taskbar where anyone can see it — a copied password must not show up on it verbatim.
/// Password managers mark copies as "do not record", so that is trusted first (<see cref="IsPasswordManager"/> plus
/// the app-side flag check), and anything missed is filtered once more by the text's shape (<see cref="LooksSecret"/>).
/// </summary>
public static class ClipboardRules
{
    /// <summary>Maximum preview length (the island shortens it further to fit its width).</summary>
    public const int PreviewLength = 24;

    private static readonly string[] PasswordManagers =
    [
        "keepass", "keepassxc", "1password", "bitwarden", "lastpass", "dashlane", "enpass", "keeper",
        "nordpass", "roboform", "protonpass", "proton pass", "passwordsafe", "pwsafe", "kee", "strongbox", "authy",
    ];

    // well-known key / token shapes (OpenAI, Anthropic, Stripe, GitHub, GitLab, Slack, AWS, Google, Hugging Face, JWT)
    private static readonly string[] TokenPrefixes =
    [
        "sk-", "sk_", "pk_live_", "rk_live_", "ghp_", "gho_", "ghu_", "ghs_", "ghr_", "github_pat_", "glpat-", "xoxb-", "xoxp-",
        "xoxa-", "xapp-", "aiza", "ya29.", "hf_", "eyj",
    ];

    private static readonly System.Text.RegularExpressions.Regex SecretAssignment = new(
        @"(?<![a-z])(pass(word|wd)?|pwd|secret|token|api[_\- ]?key|access[_\- ]?key|private[_\- ]?key|credential|비밀번호|암호)[""']?\s*[:=]",
        System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Whether it looks like a password, key or token. The island sits where anyone can see it, and IsleBar's users copy API keys
    /// all day — so this errs on the side of hiding (review 10-03: long keys, hex tokens and "API_KEY=…" lines used to show).
    /// <list type="bullet">
    /// <item>a line that assigns a password / secret / token / key (<c>API_KEY=…</c>, <c>password: …</c>);</item>
    /// <item>a single word starting like a known key or token (<c>sk-…</c>, <c>ghp_…</c>, a JWT …);</item>
    /// <item>a single word of 16+ characters mixing letters and digits (keys, hex tokens);</item>
    /// <item>8–15 characters with 3 of lowercase/uppercase/digits/symbols, or 10+ with letters and digits (passwords);</item>
    /// <item>a card number (13–19 digits, also with spaces or dashes, passing the Luhn check).</item>
    /// </list>
    /// URLs, email addresses and file paths are not secrets even with symbols — masking them would make the copy confirmation useless.
    /// </summary>
    public static bool LooksSecret(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        var value = text.Trim();
        if (SecretAssignment.IsMatch(value) || IsCardNumber(value) || UrlCredentials.IsMatch(value))
        {
            return true;
        }

        if (value.Any(char.IsWhiteSpace))
        {
            // several words: a sentence, unless one word is shaped like a key — "sk-proj-… (expires 30d)", two keys on two
            // lines (review 10-03: any space let the key through). Short passwords among words can't be told from prose.
            return value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Any(IsKeyShaped);
        }

        if (value.Length < 6)
        {
            return false;
        }

        if (value.All(char.IsAsciiDigit))
        {
            return false;   // prices, dates, phone numbers, timestamps, ids — card numbers were caught above
        }

        if (LooksLikeUrlOrPath(value))
        {
            return false;
        }

        if (value.Length >= 12 && TokenPrefixes.Any(p => value.StartsWith(p, StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        // AWS access keys: AKIA / ASIA + 16 upper-case letters or digits (a lowercase "asia-northeast3" is a region)
        if (value.Length == 20 && (value.StartsWith("AKIA", StringComparison.Ordinal) || value.StartsWith("ASIA", StringComparison.Ordinal))
            && value.All(c => char.IsAsciiDigit(c) || char.IsAsciiLetterUpper(c)))
        {
            return true;
        }

        // a file name ("IMG_20261003.jpg", "report2026.pdf"): letters, digits, _ - . and a short extension
        if (FileName.IsMatch(value))
        {
            return false;
        }

        if (value.Length < 8)
        {
            return false;
        }


        var lower = value.Any(char.IsLower);
        var upper = value.Any(char.IsUpper);
        var digit = value.Any(char.IsDigit);
        var symbol = value.Any(c => !char.IsLetterOrDigit(c));
        var letters = lower || upper;
        if (letters && digit && value.Length >= 10)
        {
            return true;
        }

        return (lower ? 1 : 0) + (upper ? 1 : 0) + (digit ? 1 : 0) + (symbol ? 1 : 0) >= 3 && value.Length <= 64
               || (letters && digit && value.Length >= 16);
    }

    /// <summary>Whether the copying program (exe name, with or without extension) is a password manager.</summary>
    public static bool IsPasswordManager(string? processName)
    {
        if (string.IsNullOrWhiteSpace(processName))
        {
            return false;
        }

        var name = Path.GetFileNameWithoutExtension(processName.Trim()).ToLowerInvariant();
        return PasswordManagers.Any(p => name == p || (p.Length >= 5 && name.StartsWith(p, StringComparison.Ordinal)));
    }

    /// <summary>
    /// The single line shown on the island: newlines/tabs become one space, surrounding whitespace is trimmed, and long text is cut at <paramref name="max"/> characters with "…".
    /// Surrogate pairs (emoji) are never split. Masked with dots if it looks like a password.
    /// </summary>
    public static string Preview(string? text, bool fromPasswordManager = false, int max = PreviewLength)
    {
        if (fromPasswordManager || LooksSecret(text))
        {
            return NoticeText.Masked;
        }

        var sb = new StringBuilder();
        var lastSpace = true;
        foreach (var c in text ?? string.Empty)
        {
            if (char.IsWhiteSpace(c) || char.IsControl(c))
            {
                if (!lastSpace)
                {
                    sb.Append(' ');
                    lastSpace = true;
                }

                continue;
            }

            sb.Append(c);
            lastSpace = false;
        }

        var line = sb.ToString().Trim();
        if (line.Length <= max)
        {
            return line;
        }

        var cut = max;
        if (char.IsHighSurrogate(line[cut - 1]))
        {
            cut--;
        }

        return line[..cut].TrimEnd() + "…";
    }

    // a file name with a common extension — a password like "Hunter2.pw" shouldn't pass as one (review 10-03)
    private static readonly System.Text.RegularExpressions.Regex FileName = new(
        @"^[\p{L}\p{N}_\-. ]+\.(jpe?g|png|gif|webp|heic|bmp|svg|pdf|docx?|xlsx?|pptx?|hwpx?|txt|md|csv|json|xml|ya?ml|html?|css|js|ts|tsx|cs|py|java|cpp|h|zip|7z|rar|mp[34]|mov|mkv|wav|flac|exe|msi|log)$",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant | System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    // a Unix / relative path of at least two parts made of path characters (a key that happens to start with "/" isn't one)
    private static readonly System.Text.RegularExpressions.Regex UnixPath = new(
        @"^(~|\.{1,2})?/[\w.\-]+(/[\w.\-]*)+$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static bool IsCardNumber(string value)
    {
        if (value.Length > 23 || value.Any(c => !(char.IsAsciiDigit(c) || c is ' ' or '-')))
        {
            return false;
        }

        var digits = value.Where(char.IsAsciiDigit).Select(c => c - '0').ToArray();
        if (digits.Length is < 13 or > 19)
        {
            return false;
        }

        var sum = 0;
        for (var i = 0; i < digits.Length; i++)
        {
            var d = digits[digits.Length - 1 - i];
            if (i % 2 == 1)
            {
                d *= 2;
                if (d > 9)
                {
                    d -= 9;
                }
            }

            sum += d;
        }

        return sum % 10 == 0;
    }

    // a URL carrying a user name and password ("postgres://admin:hunter2@db/x", "https://me:ghp_…@github.com") — review 10-03
    private static readonly System.Text.RegularExpressions.Regex UrlCredentials = new(
        @"[a-z][a-z0-9+.\-]*://[^/\s:@]+:[^/\s@]+@", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // name@domain.tld with a letters-only top-level domain — "P@ssw0rd.1" and "Kim@1990.06" are passwords, not addresses (review 10-03)
    private static readonly System.Text.RegularExpressions.Regex Email = new(
        @"^[\p{L}\p{N}._%+\-]+@[\p{L}\p{N}\-]+(\.[\p{L}\p{N}\-]+)*\.\p{L}{2,}$", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    private static readonly System.Text.RegularExpressions.Regex DomainPath = new(
        @"^[a-z0-9\-]+(\.[a-z0-9\-]+)*\.[a-z]{2,}/", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>One word that is clearly a key or token: a known prefix, an AWS key, or a long run mixing letters and digits.</summary>
    private static bool IsKeyShaped(string word)
    {
        var w = word.Trim('"', '\'', '(', ')', ',', ';', '[', ']', '<', '>');
        if (w.Length < 12 || LooksLikeUrlOrPath(w))
        {
            return false;
        }

        // a piece of a path or address split at a space ("…\OneDrive - Personal\Documents\report2026.pdf",
        // "github.com/me/repo/x2.cs"): keys don't hold backslashes, and a slash run ending in a file or starting at a domain is a path
        var slash = w.LastIndexOf('/');
        if (w.Contains('\\') || (slash >= 0 && (FileName.IsMatch(w[(slash + 1)..]) || DomainPath.IsMatch(w))))
        {
            return false;
        }

        return TokenPrefixes.Any(p => w.StartsWith(p, StringComparison.OrdinalIgnoreCase))
               || (w.Length == 20 && (w.StartsWith("AKIA", StringComparison.Ordinal) || w.StartsWith("ASIA", StringComparison.Ordinal)))
               || (w.Length >= 24 && w.Any(char.IsLetter) && w.Any(char.IsDigit) && !FileName.IsMatch(w));
    }

    private static bool LooksLikeUrlOrPath(string value)
        => value.Contains("://", StringComparison.Ordinal)
           || value.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
           || (value.Length > 2 && value[1] == ':' && value[2] is '\\' or '/')
           || value.StartsWith(@"\\", StringComparison.Ordinal)
           || UnixPath.IsMatch(value)
           || IsEmail(value);

    private static bool IsEmail(string value) => Email.IsMatch(value);
}
