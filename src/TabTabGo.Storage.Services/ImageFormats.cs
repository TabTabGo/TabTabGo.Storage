using SkiaSharp;

namespace TabTabGo.Storage.Services;

/// <summary>Image formats the thumbnail pipeline can read and write.</summary>
public static class ImageFormats
{
    // Skia encodes only these three; GIF/BMP/ICO sources are read but their thumbnails are written as PNG.
    private static readonly Dictionary<string, SKEncodedImageFormat> Encodable = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = SKEncodedImageFormat.Png,
        ["jpg"] = SKEncodedImageFormat.Jpeg,
        ["jpeg"] = SKEncodedImageFormat.Jpeg,
        ["webp"] = SKEncodedImageFormat.Webp,
    };

    private static readonly HashSet<string> ReadOnlyFormats = new(StringComparer.OrdinalIgnoreCase)
    {
        "gif", "bmp", "ico",
    };

    /// <summary>Returns whether an image with this extension can be processed.</summary>
    /// <param name="extension">Extension with or without the dot.</param>
    /// <returns><c>true</c> when supported.</returns>
    public static bool IsSupported(string? extension)
    {
        var ext = Trim(extension);
        return Encodable.ContainsKey(ext) || ReadOnlyFormats.Contains(ext);
    }

    /// <summary>The extension a thumbnail of an image with this extension is stored with (PNG for GIF/BMP/ICO).</summary>
    /// <param name="extension">Source extension, with or without the dot; the result keeps the same dot convention.</param>
    /// <returns>The thumbnail extension.</returns>
    public static string GetThumbnailExtension(string extension)
    {
        ArgumentNullException.ThrowIfNull(extension);
        var hasDot = extension.StartsWith('.');
        var ext = Trim(extension);
        var result = ReadOnlyFormats.Contains(ext) ? "png" : ext;
        return hasDot ? "." + result : result;
    }

    internal static bool TryGetEncoder(string? extension, out SKEncodedImageFormat format)
    {
        var ext = Trim(extension);
        if (Encodable.TryGetValue(ext, out format))
        {
            return true;
        }

        if (ReadOnlyFormats.Contains(ext))
        {
            format = SKEncodedImageFormat.Png;
            return true;
        }

        format = default;
        return false;
    }

    private static string Trim(string? extension) => (extension ?? string.Empty).TrimStart('.');
}
