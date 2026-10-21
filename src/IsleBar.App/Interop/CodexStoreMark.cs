using IsleBar.Core.Ui;
using Microsoft.Win32;

namespace IsleBar.App.Interop;

/// <summary>
/// The Codex mark taken from OpenAI's ChatGPT app (Microsoft Store), for PCs without the VS Code extension.
/// Nothing of OpenAI's is shipped: the app's own tile logo is read from its install folder on every run and turned into a
/// single-color mask (<see cref="PlateMask"/>). Off the UI thread; the caller paints the mask with <see cref="ClaudeGlyph.Paint"/>.
/// </summary>
internal static class CodexStoreMark
{
    /// <summary>Where Windows lists the current user's Store packages and their install folders.</summary>
    private const string Packages = @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages";

    /// <summary>A <paramref name="pixelSize"/> square alpha mask of the mark, or null when no OpenAI app is installed or its logo can't be read.</summary>
    public static byte[]? ReadMask(int pixelSize)
    {
        try
        {
            if (CodexMark.FindStoreLogo(PackageRoots()) is not { } logo)
            {
                return null;
            }

            return ReadMaskAsync(logo, pixelSize).GetAwaiter().GetResult();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)   // a missing or odd package just means "no mark"
        {
            return null;
        }
    }

    /// <summary>Install folders of the current user's OpenAI Store packages.</summary>
    private static List<string> PackageRoots()
    {
        var roots = new List<string>();
        using var packages = Registry.CurrentUser.OpenSubKey(Packages);
        if (packages is null)
        {
            return roots;
        }

        foreach (var name in packages.GetSubKeyNames())
        {
            if (!name.StartsWith(CodexMark.StorePackagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            using var package = packages.OpenSubKey(name);
            if (package?.GetValue("PackageRootFolder") is string root && Directory.Exists(root))
            {
                roots.Add(root);
            }
        }

        return roots;
    }

    private static async Task<byte[]?> ReadMaskAsync(string png, int pixelSize)
    {
        // read with plain file I/O: an unpackaged app may open files under WindowsApps but not broker them as StorageFiles
        using var stream = new MemoryStream(File.ReadAllBytes(png)).AsRandomAccessStream();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var pixels = await decoder.GetPixelDataAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Straight,
            new Windows.Graphics.Imaging.BitmapTransform(),
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        return PlateMask.Build(pixels.DetachPixelData(), (int)decoder.PixelWidth, (int)decoder.PixelHeight, pixelSize);
    }
}
