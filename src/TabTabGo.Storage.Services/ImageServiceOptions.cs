namespace TabTabGo.Storage.Services;

/// <summary>Options for <see cref="ImageService"/>. Bind from the <c>ImageService</c> configuration section.</summary>
public class ImageServiceOptions
{
    /// <summary>
    /// Largest accepted image in pixels (width x height), checked from the header before decoding. Guards against
    /// decompression bombs: a few kilobytes of PNG can declare gigapixels. Defaults to 50 000 000.
    /// </summary>
    public long MaxPixels { get; set; } = 50_000_000;

    /// <summary>JPEG/WEBP encoder quality, 1-100. Defaults to 90.</summary>
    public int JpegQuality { get; set; } = 90;
}
