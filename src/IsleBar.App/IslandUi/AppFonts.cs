using IsleBar.Core.Localization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace IsleBar.App.IslandUi;

/// <summary>
/// App fonts: the bundled Pretendard (no install needed) + per-language Windows fonts for Han characters and kana (UiFonts.Stack).
/// Not just the island and input box — the options, settings and search-results windows use the same font. When only the island changed,
/// the windows stayed on Segoe UI and looked out of place, and in Japanese, kanji like "起動" rendered in a Korean-style font (measured on PC 09-30).
/// </summary>
internal static class AppFonts
{
    private static readonly Dictionary<string, FontFamily> Cache = [];

    /// <summary>Regular weight.</summary>
    public static readonly string Regular = Bundled("Pretendard-Regular.otf", "Pretendard");

    /// <summary>
    /// Emphasis weight — Medium (500). SemiBold (600) was too heavy for small taskbar text (user feedback 09-30).
    /// Each weight is a separate file: with the variable font (one file), weights didn't take effect.
    /// </summary>
    public static readonly string Medium = Bundled("Pretendard-Medium.otf", "Pretendard Medium");

    private static string Bundled(string file, string family)
    {
        // The internal name differs per weight ("Pretendard", "Pretendard Medium") — you must use that name to get that weight.
        // WinUI couldn't read file:/// paths and fell back to Malgun Gothic (measured on PC 09-30) — use ms-appx:/// relative to the app folder.
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts", file);
        return File.Exists(path) ? $"ms-appx:///Assets/Fonts/{file}#{family}" : family;
    }

    public static FontFamily Family(string stack)
    {
        if (!Cache.TryGetValue(stack, out var family))
        {
            Cache[stack] = family = new FontFamily(stack);
        }

        return family;
    }

    /// <summary>Regular and emphasis fonts suited to <paramref name="text"/> (Japanese font first if it contains kana, etc.).</summary>
    public static (FontFamily Normal, FontFamily Strong) For(string? text, string? language)
        => (Family(UiFonts.Stack(text, language, Regular)), Family(UiFonts.Stack(text, language, Medium)));

    /// <summary>
    /// Applies the font to the whole window: set on the outermost control, inner text inherits it. Text set bold (SemiBold or heavier)
    /// uses the emphasis font (Medium) — asking the regular-weight file for bold yields a blurry fake bold.
    /// If the window content is a panel (not a control) it can't pass it down, so it is wrapped once in a ContentControl.
    /// </summary>
    public static void Apply(Window window, string? language)
    {
        var (normal, strong) = For(null, language);
        if (window.Content is not Control root)
        {
            if (window.Content is not FrameworkElement inner)
            {
                return;
            }

            window.Content = null;   // must detach from the window first — wrapping it while attached crashed the app with an "already a child elsewhere" error (measured on PC 09-30)
            root = new ContentControl
            {
                Content = inner,
                IsTabStop = false,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                VerticalContentAlignment = VerticalAlignment.Stretch,
            };
            window.Content = root;
        }

        root.FontFamily = normal;
        Strengthen(root, strong);
    }

    private static void Strengthen(object? node, FontFamily strong)
    {
        switch (node)
        {
            case TextBlock text when text.FontWeight.Weight >= 600:
                text.FontFamily = strong;
                text.FontWeight = Microsoft.UI.Text.FontWeights.Medium;
                break;
            case Panel panel:
                foreach (var child in panel.Children)
                {
                    Strengthen(child, strong);
                }

                break;
            case Border border:
                Strengthen(border.Child, strong);
                break;
            case ScrollViewer scroll:
                Strengthen(scroll.Content, strong);
                break;
            case ContentControl content:
                Strengthen(content.Content, strong);
                break;
        }
    }
}
