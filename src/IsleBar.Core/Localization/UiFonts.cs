namespace IsleBar.Core.Localization;

/// <summary>
/// Default Windows UI font per language. Drawing Japanese/Chinese with a Korean font gives awkward spacing.
/// Icons use Segoe Fluent Icons regardless of language — no third-party fonts or icons (public release rule).
/// </summary>
public static class UiFonts
{
    /// <summary>Icon font. Ships with Windows 11.</summary>
    public const string Icon = "Segoe Fluent Icons";

    /// <summary>General UI font for options, settings, notifications, etc.</summary>
    public static string Text(string? language) => language switch
    {
        "ko" => "Malgun Gothic",
        "ja" => "Yu Gothic UI",
        "zh" => "Microsoft YaHei UI",
        "zht" => "Microsoft JhengHei UI",
        _ => "Segoe UI",
    };

    /// <summary>
    /// Input box font. The placeholder is English, but <b>input can be in any language</b> — so even when the UI language
    /// is European, Malgun Gothic (which covers Hangul) is the default (same as the Python version's ENTRY_FONT).
    /// For the Korean IME composition text size, the app must re-apply this font with ImmSetCompositionFont.
    /// </summary>
    public static string Entry(string? language) => language switch
    {
        "ja" => "Yu Gothic UI",
        "zh" => "Microsoft YaHei UI",
        "zht" => "Microsoft JhengHei UI",
        _ => "Malgun Gothic",
    };

    /// <summary>
    /// Default font name for the island and input box: Pretendard (bundled with the app, SIL OFL). Its Latin is based on Inter, so it blends with Hangul as one.
    /// Segoe UI (English) + Malgun Gothic (Hangul) looked mismatched within a single line and the English stood out (user feedback 09-30: it was distracting,
    /// wanted a Toss-like feel). The app prefixes this name with the font file path.
    /// </summary>
    public const string Primary = "Pretendard Variable";

    /// <summary>
    /// Font list for places where <b>text in any language gets mixed in</b>, like the island and input box (in order; missing glyphs fall through to the next font).
    /// Latin, digits and Hangul use <paramref name="primary"/> (Pretendard), kana a Japanese font, and Han characters the UI language's font.
    /// </summary>
    public static string Stack(string? text, string? language, string primary = Primary)
    {
        var cjk = new List<string>();
        if (text is not null && text.Any(IsKana))
        {
            cjk.Add("Yu Gothic UI");   // Pretendard has no kana — put a Japanese font first so Japanese titles do not render with Korean-style Han glyphs
        }

        cjk.Add(Entry(language));
        cjk.AddRange(["Malgun Gothic", "Yu Gothic UI", "Microsoft YaHei UI", "Microsoft JhengHei UI"]);
        return string.Join(", ", new[] { primary }.Concat(cjk).Distinct());
    }

    private static bool IsKana(char c) => c is >= '぀' and <= 'ヿ';

}
