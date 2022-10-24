using System.Collections.Generic;
using System.Drawing;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;

public static class ImageSizeExtensions
{
    private static Dictionary<ImageSize, Size> _sizePresets = new Dictionary<ImageSize, Size>
        {
                { ImageSize.Default, new Size() {Height = 1000, Width = 1800} },
                { ImageSize.ThumbLarge, new Size() {Height = 180, Width = 180} },
                { ImageSize.ThumbMedium, new Size() {Height = 160, Width = 160} },
                { ImageSize.ThumbSmall, new Size() {Height = 120, Width = 120} },
                //{ ImageSize.IconLarge, new Size() {Height = 64, Width = 64} },
                //{ ImageSize.IconMedium, new Size() {Height = 32, Width = 32} },
                //{ ImageSize.IconSmall, new Size() {Height = 16, Width = 16} }
        };

    public static Size GetSize(this ImageSize sizeEnum)
    {
        if (_sizePresets.ContainsKey(sizeEnum))
            return _sizePresets[sizeEnum];
        else
            throw new TTGException($"Image size {sizeEnum.ToString()} is not supported", code: "IMAGE_SIZE_NOT_SUPPORTED");
    }
}