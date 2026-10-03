namespace IsleBar.Core.Localization;

/// <summary>
/// Decides the language to use. If the setting is "auto", it is decided from the Windows display language (LANGID).
/// Obtaining the LANGID (<c>GetUserDefaultUILanguage</c>) is done by the app; this only takes the number —
/// so it can be tested on Linux too.
/// </summary>
public static class LanguageResolver
{
    // primary language ID (low 10 bits of LANGID) → our language code
    private const int LangChinese = 0x04;
    private const int LangKorean = 0x12;
    private const int LangJapanese = 0x11;
    private const int LangFrench = 0x0C;
    private const int LangGerman = 0x07;
    private const int LangItalian = 0x10;
    private const int LangPortuguese = 0x16;
    private const int LangSpanish = 0x0A;

    /// <summary>Full LANGIDs of regions using Traditional Chinese — Taiwan, Hong Kong, Macau.</summary>
    private static readonly int[] TraditionalChinese = [0x0404, 0x0C04, 0x1404];

    /// <summary>
    /// Decides the language from the setting <paramref name="preference"/> ("auto" or a language code) and the Windows LANGID.
    /// </summary>
    public static string Resolve(string? preference, int langId)
    {
        if (LanguageCatalog.IsKnown(preference))
        {
            return preference!;
        }

        return FromLangId(langId);
    }

    /// <summary>Decides from the Windows LANGID alone.</summary>
    public static string FromLangId(int langId)
    {
        var primary = langId & 0x3FF;
        if (primary == LangChinese)
        {
            // Taiwan, Hong Kong and Macau get Traditional; the rest (mainland China, Singapore) get Simplified
            return Array.IndexOf(TraditionalChinese, langId & 0xFFFF) >= 0 ? "zht" : "zh";
        }

        return primary switch
        {
            LangKorean => "ko",
            LangJapanese => "ja",
            LangFrench => "fr",
            LangGerman => "de",
            LangItalian => "it",
            LangPortuguese => "pt",
            LangSpanish => "es",
            _ => LanguageCatalog.Fallback,
        };
    }
}
