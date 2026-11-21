using System.Drawing;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SkiaSharp;
using TabTabGo.Core.Exceptions;

namespace TabTabGo.Storage.Services;

/// <summary>
/// SkiaSharp implementation of <see cref="IImageService"/>. Reads the header first and refuses images over the pixel
/// budget before decoding, applies EXIF orientation, and encodes JPEG/PNG/WEBP (GIF/BMP/ICO sources become PNG).
/// </summary>
public class ImageService : IImageService
{
    private readonly ImageServiceOptions _options;

    /// <summary>Creates the service with default options.</summary>
    public ImageService()
        : this(null)
    {
    }

    /// <summary>Creates the service.</summary>
    /// <param name="options">Image options; defaults are used when <c>null</c>.</param>
    public ImageService(IOptions<ImageServiceOptions>? options)
    {
        _options = options?.Value ?? new ImageServiceOptions();
    }

    /// <inheritdoc />
    public void Resize(string inputPath, Size outputSize, string outputPath, string outputFormat, bool cropSquare,
        Rectangle? squareFrame)
    {
        using var inputStream = File.OpenRead(inputPath);
        Resize(inputStream, outputSize, outputPath, outputFormat, cropSquare, squareFrame);
    }

    /// <inheritdoc />
    public void Resize(Stream inputStream, Size outputSize, string outputPath, string outputFormat, bool cropSquare,
        Rectangle? squareFrame)
    {
        // Validate before touching the disk so a rejected image never leaves a file behind.
        EnsureSupported(outputFormat);

        // Encode to memory first: a corrupt or oversized image must never truncate or create the target file.
        using var buffer = new MemoryStream();
        Resize(inputStream, outputSize, buffer, outputFormat, cropSquare, squareFrame);

        // FileMode.Create truncates; OpenWrite would leave trailing bytes when overwriting a larger file.
        using (var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write))
        {
            buffer.Position = 0;
            buffer.CopyTo(output);
        }

        if (!File.Exists(outputPath))
        {
            throw Error(HttpStatusCode.InternalServerError, "IMAGE_RESIZE_FAILED",
                $"Image resize failed. width: {outputSize.Width} height: {outputSize.Height}");
        }
    }

    /// <inheritdoc />
    public void Resize(Stream inputStream, Size outputSize, Stream outputStream, string outputFormat, bool cropSquare,
        Rectangle? squareFrame)
    {
        ArgumentNullException.ThrowIfNull(inputStream);
        ArgumentNullException.ThrowIfNull(outputStream);
        var format = EnsureSupported(outputFormat);

        var source = inputStream;
        MemoryStream? copy = null;
        try
        {
            if (!inputStream.CanSeek)
            {
                copy = new MemoryStream();
                inputStream.CopyTo(copy);
                copy.Position = 0;
                source = copy;
            }

            using var codec = SKCodec.Create(source)
                ?? throw Error(HttpStatusCode.BadRequest, "IMAGE_NOT_DECODABLE", "The file is not a readable image.");

            var info = codec.Info;
            if ((long)info.Width * info.Height > _options.MaxPixels)
            {
                throw Error(HttpStatusCode.BadRequest, "IMAGE_TOO_LARGE",
                    $"The image is {info.Width} x {info.Height} pixels; the limit is {_options.MaxPixels} pixels.");
            }

            using var decoded = SKBitmap.Decode(codec)
                ?? throw Error(HttpStatusCode.BadRequest, "IMAGE_NOT_DECODABLE", "The file is not a readable image.");
            using var oriented = ApplyOrigin(decoded, codec.EncodedOrigin);
            var upright = oriented ?? decoded;

            using var cropped = cropSquare ? Crop(upright, squareFrame) : null;
            var toResize = cropped ?? upright;

            using var resized = toResize.Resize(new SKImageInfo(outputSize.Width, outputSize.Height),
                new SKSamplingOptions(SKCubicResampler.Mitchell))
                ?? throw Error(HttpStatusCode.InternalServerError, "IMAGE_RESIZE_FAILED",
                    $"Image resize failed. width: {outputSize.Width} height: {outputSize.Height}");

            using var image = SKImage.FromBitmap(resized);
            using var data = image.Encode(format, Math.Clamp(_options.JpegQuality, 1, 100))
                ?? throw Error(HttpStatusCode.InternalServerError, "IMAGE_RESIZE_FAILED", "Image encoding failed.");
            data.SaveTo(outputStream);
        }
        finally
        {
            copy?.Dispose();
        }
    }

    private static SKEncodedImageFormat EnsureSupported(string outputFormat)
    {
        if (string.IsNullOrWhiteSpace(outputFormat) || !ImageFormats.TryGetEncoder(outputFormat, out var format))
        {
            throw Error(HttpStatusCode.BadRequest, "IMAGE_FORMAT_NOT_SUPPORTED",
                $"Image format {outputFormat} is not supported");
        }

        return format;
    }

    // Returns a new bitmap when a transform is needed, otherwise null (the caller keeps using the original).
    private static SKBitmap? ApplyOrigin(SKBitmap source, SKEncodedOrigin origin)
    {
        if (origin is SKEncodedOrigin.TopLeft or SKEncodedOrigin.Default)
        {
            return null;
        }

        var w = source.Width;
        var h = source.Height;
        var swap = origin is SKEncodedOrigin.LeftTop or SKEncodedOrigin.RightTop or SKEncodedOrigin.RightBottom
            or SKEncodedOrigin.LeftBottom;
        var target = new SKBitmap(swap ? h : w, swap ? w : h);
        using var canvas = new SKCanvas(target);
        var matrix = origin switch
        {
            SKEncodedOrigin.TopRight => new SKMatrix(-1, 0, w, 0, 1, 0, 0, 0, 1),
            SKEncodedOrigin.BottomRight => new SKMatrix(-1, 0, w, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.BottomLeft => new SKMatrix(1, 0, 0, 0, -1, h, 0, 0, 1),
            SKEncodedOrigin.LeftTop => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightTop => new SKMatrix(0, -1, h, 1, 0, 0, 0, 0, 1),
            SKEncodedOrigin.RightBottom => new SKMatrix(0, -1, h, -1, 0, w, 0, 0, 1),
            SKEncodedOrigin.LeftBottom => new SKMatrix(0, 1, 0, -1, 0, w, 0, 0, 1),
            _ => SKMatrix.Identity,
        };
        canvas.Concat(in matrix);
        canvas.DrawBitmap(source, 0, 0);
        return target;
    }

    private static SKBitmap Crop(SKBitmap source, Rectangle? frame)
    {
        SKRectI rect;
        if (frame is { } f)
        {
            rect = new SKRectI(f.X, f.Y, f.Right, f.Bottom);
            if (f.Width <= 0 || f.Height <= 0 || rect.Left < 0 || rect.Top < 0 || rect.Right > source.Width ||
                rect.Bottom > source.Height)
            {
                throw Error(HttpStatusCode.BadRequest, "IMAGE_CROP_FRAME_INVALID",
                    "The crop frame is outside the image.");
            }
        }
        else
        {
            // Largest centred square; with an odd difference the extra pixel falls on the far edge.
            var side = Math.Min(source.Width, source.Height);
            var left = (source.Width - side) / 2;
            var top = (source.Height - side) / 2;
            rect = new SKRectI(left, top, left + side, top + side);
        }

        var cropped = new SKBitmap();
        if (!source.ExtractSubset(cropped, rect))
        {
            cropped.Dispose();
            throw Error(HttpStatusCode.BadRequest, "IMAGE_CROP_FRAME_INVALID", "The crop frame is outside the image.");
        }

        return cropped;
    }

    private static ApiException Error(HttpStatusCode status, string code, string message) =>
        new(message, null, status >= HttpStatusCode.InternalServerError ? LogLevel.Error : LogLevel.Warning, null,
            status, code);
}
