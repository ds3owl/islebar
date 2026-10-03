namespace IsleBar.Core.Search;

/// <summary>One web search engine: name stored in settings, display name, search URL template ({0} = query).</summary>
public sealed record WebEngine(string Id, string DisplayName, string UrlTemplate);

/// <summary>
/// The search bar's web mode (Tab cycles Claude → files → web). Pressing Enter opens the results in the default browser.
/// <b>Engines popular in each language's country</b> can be picked; the default is "auto" (the language's first engine)
/// (user feedback 09-30: Google as the default, with different engines per language). Simplified Chinese defaults to Baidu since Google is blocked in many places.
/// </summary>
public static class WebSearch
{
    /// <summary>Setting value "auto" = the per-language default engine.</summary>
    public const string Auto = "auto";

    public static readonly IReadOnlyList<WebEngine> Engines =
    [
        new("google", "Google", "https://www.google.com/search?q={0}"),
        new("bing", "Bing", "https://www.bing.com/search?q={0}"),
        new("duckduckgo", "DuckDuckGo", "https://duckduckgo.com/?q={0}"),
        new("naver", "NAVER", "https://search.naver.com/search.naver?query={0}"),
        new("daum", "Daum", "https://search.daum.net/search?q={0}"),
        new("yahoo_jp", "Yahoo! JAPAN", "https://search.yahoo.co.jp/search?p={0}"),
        new("baidu", "百度 Baidu", "https://www.baidu.com/s?wd={0}"),
        new("bing_cn", "必应 Bing", "https://cn.bing.com/search?q={0}"),
        new("sogou", "搜狗 Sogou", "https://www.sogou.com/web?query={0}"),
        new("yahoo_tw", "Yahoo 奇摩", "https://tw.search.yahoo.com/search?p={0}"),
        new("qwant", "Qwant", "https://www.qwant.com/?q={0}"),
        new("ecosia", "Ecosia", "https://www.ecosia.org/search?q={0}"),
    ];

    /// <summary>Engines selectable per language. The first is that language's default.</summary>
    private static readonly IReadOnlyDictionary<string, string[]> ByLanguage = new Dictionary<string, string[]>
    {
        ["ko"] = ["google", "naver", "daum", "bing"],
        ["ja"] = ["google", "yahoo_jp", "bing"],
        ["zh"] = ["baidu", "bing_cn", "sogou"],
        ["zht"] = ["google", "yahoo_tw", "bing"],
        ["fr"] = ["google", "qwant", "bing", "duckduckgo"],
        ["de"] = ["google", "ecosia", "bing", "duckduckgo"],
        ["en"] = ["google", "bing", "duckduckgo"],
        ["it"] = ["google", "bing", "duckduckgo"],
        ["pt"] = ["google", "bing", "duckduckgo"],
        ["es"] = ["google", "bing", "duckduckgo"],
    };

    public static bool IsKnown(string? id) => id == Auto || Engines.Any(e => e.Id == id);

    /// <summary>Engines selectable in this language (unknown languages get the English list). The first is the default.</summary>
    public static IReadOnlyList<WebEngine> EnginesFor(string? language)
        => (ByLanguage.TryGetValue(language ?? string.Empty, out var ids) ? ids : ByLanguage["en"])
            .Select(id => Engines.First(e => e.Id == id))
            .ToList();

    /// <summary>
    /// The engine actually used. "auto" (or an unknown value) gives the per-language default; an explicitly chosen engine stays even if the language changes.
    /// </summary>
    public static WebEngine Resolve(string? setting, string? language)
        => Engines.FirstOrDefault(e => e.Id == setting) ?? EnginesFor(language)[0];

    /// <summary>Search URL. The query is URL-encoded (so Hangul, spaces, &amp;, # etc. do not break). Null for an empty query.</summary>
    public static Uri? UrlFor(WebEngine engine, string? query)
    {
        ArgumentNullException.ThrowIfNull(engine);
        var trimmed = query?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return null;
        }

        return new Uri(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            engine.UrlTemplate,
            Uri.EscapeDataString(trimmed)));
    }
}
