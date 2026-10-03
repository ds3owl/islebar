using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using IsleBar.Core.Ui;
using Microsoft.UI.Xaml.Media.Imaging;

namespace IsleBar.App.Interop;

/// <summary>
/// Claude spark icon at the left of the search box. <b>Extracted on every run from the official icon of the installed claude.exe</b>
/// (no separate image file is shipped — same approach as the Python version). If extraction fails, null → the magnifier is used instead.
/// </summary>
internal static class ClaudeGlyph
{
    private const int SourceSize = 256;

    /// <summary>Extracts only the spark from <paramref name="exePath"/>'s icon and paints it in the single color <paramref name="color"/>.</summary>
    public static WriteableBitmap? Create(string exePath, int pixelSize, Windows.UI.Color color)
    {
        if (!File.Exists(exePath) || ReadIconPixels(exePath) is not { } bgra)
        {
            return null;
        }

        if (GlyphMask.Build(bgra, SourceSize, SourceSize, pixelSize) is not { } alpha)
        {
            return null;
        }

        // Premultiplied BGRA
        var pixels = new byte[pixelSize * pixelSize * 4];
        for (var i = 0; i < alpha.Length; i++)
        {
            var a = alpha[i];
            pixels[(i * 4) + 0] = (byte)(color.B * a / 255);
            pixels[(i * 4) + 1] = (byte)(color.G * a / 255);
            pixels[(i * 4) + 2] = (byte)(color.R * a / 255);
            pixels[(i * 4) + 3] = a;
        }

        var bitmap = new WriteableBitmap(pixelSize, pixelSize);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(pixels, 0, pixels.Length);
        }

        bitmap.Invalidate();
        return bitmap;
    }

    /// <summary>Reads the 256×256 icon as top-down 32-bit BGRA.</summary>
    private static byte[]? ReadIconPixels(string exePath)
    {
        var icons = new IntPtr[1];
        var ids = new int[1];
        if (PrivateExtractIconsW(exePath, 0, SourceSize, SourceSize, icons, ids, 1, 0) < 1 || icons[0] == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            if (!GetIconInfo(icons[0], out var info))
            {
                return null;
            }

            try
            {
                var header = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = SourceSize,
                    biHeight = -SourceSize,   // negative = top-down
                    biPlanes = 1,
                    biBitCount = 32,
                };
                var pixels = new byte[SourceSize * SourceSize * 4];
                var dc = GetDC(IntPtr.Zero);
                try
                {
                    return GetDIBits(dc, info.hbmColor, 0, SourceSize, pixels, ref header, 0) == SourceSize ? pixels : null;
                }
                finally
                {
                    ReleaseDC(IntPtr.Zero, dc);
                }
            }
            finally
            {
                DeleteObject(info.hbmColor);
                DeleteObject(info.hbmMask);
            }
        }
        finally
        {
            DestroyIcon(icons[0]);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)] public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
        // 32-bit has no color table, but GetDIBits may write up to the BITMAPINFO size, so leave room
        public int pad0, pad1, pad2, pad3;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int PrivateExtractIconsW(string file, int index, int cx, int cy,
        [Out] IntPtr[] icons, [Out] int[] ids, int count, int flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetIconInfo(IntPtr icon, out ICONINFO info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr icon);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr dc);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr dc, IntPtr bitmap, int start, int lines,
        [Out] byte[] bits, ref BITMAPINFOHEADER info, int usage);

    [DllImport("gdi32.dll", EntryPoint = "DeleteObject")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr handle);
}
