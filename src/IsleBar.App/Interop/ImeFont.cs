using System.Runtime.InteropServices;

namespace IsleBar.App.Interop;

/// <summary>
/// Sets the font and size the Korean IME uses to draw characters being composed.
/// Without it, only the composing characters use the system default size and don't match the committed text
/// (actually hit on 09-29).
/// </summary>
internal static partial class ImeFont
{
    private const int LF_FACESIZE = 32;

    /// <summary>Applies the input box's font and size to IME composition characters too.</summary>
    /// <param name="window">Handle of the window receiving input.</param>
    /// <param name="faceName">Font name (<c>UiFonts.Entry(language)</c>).</param>
    /// <param name="pixelHeight">Character height in pixels, already multiplied by the scale.</param>
    public static bool Apply(IntPtr window, string faceName, int pixelHeight)
    {
        var context = ImmGetContext(window);
        if (context == IntPtr.Zero)
        {
            return false;   // no IME in this environment (e.g. English-only)
        }

        try
        {
            var font = new LOGFONTW
            {
                lfHeight = -Math.Abs(pixelHeight),   // negative = character height (excluding internal leading)
                lfCharSet = 1,                        // DEFAULT_CHARSET
                lfFaceName = faceName.Length >= LF_FACESIZE ? faceName[..(LF_FACESIZE - 1)] : faceName,
            };

            return ImmSetCompositionFontW(context, ref font);
        }
        finally
        {
            ImmReleaseContext(window, context);
        }
    }

    [LibraryImport("imm32.dll")]
    private static partial IntPtr ImmGetContext(IntPtr window);

    [LibraryImport("imm32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ImmReleaseContext(IntPtr window, IntPtr context);

    // Source-generated P/Invoke can't handle the fixed-length string (ByValTStr) inside LOGFONTW, so only this one uses DllImport (SYSLIB1051).
    [DllImport("imm32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImmSetCompositionFontW(IntPtr context, ref LOGFONTW font);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONTW
    {
        public int lfHeight;
        public int lfWidth;
        public int lfEscapement;
        public int lfOrientation;
        public int lfWeight;
        public byte lfItalic;
        public byte lfUnderline;
        public byte lfStrikeOut;
        public byte lfCharSet;
        public byte lfOutPrecision;
        public byte lfClipPrecision;
        public byte lfQuality;
        public byte lfPitchAndFamily;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = LF_FACESIZE)]
        public string lfFaceName;
    }
}
