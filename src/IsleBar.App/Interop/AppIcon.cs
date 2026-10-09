using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.Graphics.Imaging;

namespace IsleBar.App.Interop;

/// <summary>
/// The real icon of an app, from its AUMID — so the expanded card can show, say, WhatsApp's own icon instead of a generic
/// bubble (user 09-30). The icon is the vendor's own (asked for through the shell), never one we draw.
/// <para>
/// The shell hands the icon back as a GDI bitmap (HBITMAP); we copy its pixels into a WinRT <see cref="SoftwareBitmap"/> and
/// wrap it as an image source, with no temp files. Results are cached per AUMID on the UI thread. Any failure returns null and
/// the caller falls back to the glyph.
/// </para>
/// </summary>
internal static class AppIcon
{
    private const int Size = 48;           // asked-for icon size in pixels
    private const int SIIGBF_ICONONLY = 0x4;
    private const uint DIB_RGB_COLORS = 0;
    private const uint BI_RGB = 0;

    private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The bitmaps behind the cached sources, kept open for good. XAML copies a SoftwareBitmapSource's pixels to the screen
    /// again later (its surface is dropped and rebuilt), and reading a bitmap we had disposed failed fast with RO_E_CLOSED
    /// inside RTMediaFrame — the bar crashed about once a day after hours of running (dumps 10-05..10-08). 48 px icons are tiny.
    /// </summary>
    private static readonly List<SoftwareBitmap> KeepAlive = [];

    /// <summary>
    /// Calls <paramref name="apply"/> with the app's icon once it is ready (immediately if cached). Never throws; if the icon
    /// can't be had, it simply never calls back and the glyph the caller already showed stays.
    /// </summary>
    public static async void Resolve(string? aumid, Action<ImageSource> apply)
    {
        if (string.IsNullOrWhiteSpace(aumid))
        {
            return;
        }

        if (Cache.TryGetValue(aumid, out var cached))
        {
            if (cached is not null)
            {
                apply(cached);
            }

            return;
        }

        ImageSource? source = null;
        try
        {
            var bitmap = LoadSoftwareBitmap(aumid);
            if (bitmap is not null)
            {
                var wic = new SoftwareBitmapSource();
                await wic.SetBitmapAsync(bitmap);
                KeepAlive.Add(bitmap);   // never Dispose: see KeepAlive
                source = wic;
            }
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException or OutOfMemoryException)
        {
            source = null;
        }

        Cache[aumid] = source;
        if (source is not null)
        {
            apply(source);
        }
    }

    private static SoftwareBitmap? LoadSoftwareBitmap(string aumid)
    {
        var factoryIid = typeof(IShellItemImageFactory).GUID;
        if (SHCreateItemFromParsingName(@"shell:AppsFolder\" + aumid, IntPtr.Zero, ref factoryIid, out var factoryObject) != 0 || factoryObject is null)
        {
            return null;
        }

        var factory = (IShellItemImageFactory)factoryObject;
        var hbitmap = IntPtr.Zero;
        try
        {
            var size = new SIZE { cx = Size, cy = Size };
            if (factory.GetImage(size, SIIGBF_ICONONLY, out hbitmap) != 0 || hbitmap == IntPtr.Zero)
            {
                return null;
            }

            return FromHBitmap(hbitmap);
        }
        finally
        {
            if (hbitmap != IntPtr.Zero)
            {
                DeleteObject(hbitmap);
            }

            Marshal.ReleaseComObject(factory);
        }
    }

    private static SoftwareBitmap? FromHBitmap(IntPtr hbitmap)
    {
        var bmp = default(BITMAP);
        if (GetObject(hbitmap, Marshal.SizeOf<BITMAP>(), ref bmp) == 0 || bmp.bmWidth <= 0 || bmp.bmHeight <= 0)
        {
            return null;
        }

        var w = bmp.bmWidth;
        var h = bmp.bmHeight;
        var pixels = new byte[w * h * 4];
        var info = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = w,
            biHeight = -h,   // negative = top-down, so rows are in the order WinRT expects
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };

        var hdc = GetDC(IntPtr.Zero);
        try
        {
            if (GetDIBits(hdc, hbitmap, 0, (uint)h, pixels, ref info, DIB_RGB_COLORS) == 0)
            {
                return null;
            }
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, hdc);
        }

        // The shell returns 32bpp with premultiplied alpha; GetDIBits gives BGRA byte order, which matches Bgra8.
        return SoftwareBitmap.CreateCopyFromBuffer(pixels.AsBuffer(), BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE
    {
        public int cx;
        public int cy;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType;
        public int bmWidth;
        public int bmHeight;
        public int bmWidthBytes;
        public ushort bmPlanes;
        public ushort bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr bindContext, ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out object? item);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(IntPtr handle, int count, ref BITMAP bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbmp, uint start, uint lines, byte[] bits, ref BITMAPINFOHEADER info, uint usage);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr window);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr window, IntPtr hdc);
}
