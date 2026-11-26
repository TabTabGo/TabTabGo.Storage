using System.Drawing;
using System.Net;
using Microsoft.Extensions.Logging;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;

namespace TabTabGo.Storage.Services;

/// <summary>Maps <see cref="ImageSize"/> presets to pixel sizes.</summary>
public static class ImageSizeExtensions
{
    private static readonly Dictionary<ImageSize, Size> SizePresets = new()
    {
        { ImageSize.Default, new Size { Height = 1000, Width = 1800 } },
        { ImageSize.ThumbLarge, new Size { Height = 180, Width = 180 } },
        { ImageSize.ThumbMedium, new Size { Height = 160, Width = 160 } },
        { ImageSize.ThumbSmall, new Size { Height = 120, Width = 120 } },
    };

    /// <summary>Gets the pixel size of a preset.</summary>
    /// <param name="sizeEnum">The preset.</param>
    /// <returns>Width and height in pixels.</returns>
    /// <exception cref="ApiException">The value is not a defined preset (400).</exception>
    public static Size GetSize(this ImageSize sizeEnum)
    {
        if (SizePresets.TryGetValue(sizeEnum, out var size))
        {
            return size;
        }

        throw new ApiException($"Image size {sizeEnum} is not supported", null, LogLevel.Warning, null,
            HttpStatusCode.BadRequest, "IMAGE_SIZE_NOT_SUPPORTED");
    }
}
