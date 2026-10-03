using IsleBar.Core.Ui;
using Microsoft.Win32;
using Windows.UI;

namespace IsleBar.App.Interop;

/// <summary>
/// Theme colors. Port of the Python version's <c>winstyle.palette()</c>.
///
/// Key lesson: <b>text color is decided by the actual background brightness, not the setting.</b>
/// While the theme is switching there are moments when the registry value and the painted color disagree;
/// trusting only the setting gives white text on white and the text vanishes.
///
/// <para><b>The color rules themselves live in <see cref="ThemePalette"/> (Core)</b> — moved there so they
/// can be tested without a window (09-29). What remains here is only registry reading and Windows color conversion.</para>
/// </summary>
internal sealed record ThemeColors(
    bool Light,
    Color Accent,
    Color Foreground,
    Color Hint,
    Color Icon,
    Color Flyout,
    Color Divider,
    Color Ok,
    Color Bad)
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const string DwmKey = @"Software\Microsoft\Windows\DWM";

    /// <summary>Reads the current theme from the registry.</summary>
    public static ThemeColors FromSystem()
    {
        var light = ReadInt(PersonalizeKey, "SystemUsesLightTheme", 0) == 1;
        return From(ThemePalette.For(light, ToRgb(AccentColor())));
    }

    /// <summary>
    /// Decides text color from the <b>actually painted background</b>. Dark text if brightness (average) exceeds 140.
    /// </summary>
    public static ThemeColors ForActualBackground(Color background, Color accent)
        => From(ThemePalette.ForBackground(ToRgb(background), ToRgb(accent)));

    /// <summary>Converts Core's palette to Windows colors.</summary>
    private static ThemeColors From(ThemeColorSet set) => new(
        set.Light, ToColor(set.Accent), ToColor(set.Foreground), ToColor(set.Hint), ToColor(set.Icon),
        ToColor(set.Flyout), ToColor(set.Divider), ToColor(set.Ok), ToColor(set.Bad));

    private static Color ToColor(Rgb c) => Color.FromArgb(255, c.R, c.G, c.B);

    private static Rgb ToRgb(Color c) => new(c.R, c.G, c.B);

    /// <summary>Accent color. Stored as ABGR in the registry.</summary>
    public static Color AccentColor()
        => ToColor(ThemePalette.AccentFromDwm(ReadInt(DwmKey, "AccentColor", ThemePalette.DefaultAccentValue)));

    public static double Brightness(Color color) => ThemePalette.Brightness(ToRgb(color));

    private static int ReadInt(string key, string name, int fallback)
    {
        try
        {
            using var handle = Registry.CurrentUser.OpenSubKey(key);
            return handle?.GetValue(name) is int value ? value : fallback;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return fallback;
        }
    }
}
