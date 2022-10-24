using SkiaSharp;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Services;
using TabTabGo.Core;

namespace TabTabGo.Storage.Services;

public class ImageService : IImageService
{
    //private readonly Dictionary<ImageSize, Size> _sizePresets;
    private readonly Dictionary<string, SKEncodedImageFormat> _outputImageFormats;

    public ImageService()
    {
        _outputImageFormats = new Dictionary<string, SKEncodedImageFormat>
        {
            { "bpm", SKEncodedImageFormat.Bmp },
            { "gif", SKEncodedImageFormat.Gif },
            { "ico", SKEncodedImageFormat.Ico },
            { "jpeg", SKEncodedImageFormat.Jpeg },
            { "jpg", SKEncodedImageFormat.Jpeg },
            { "png", SKEncodedImageFormat.Png }
        };
    }

    //public Dictionary<ImageSize, Size> SizePresets => _sizePresets;

    public void Resize(string inputPath, Size outputSize, string outputPath, string outputFormat, bool cropSquare,
        Rectangle? squareFrame)
    {
        using FileStream inputStream = File.OpenRead(inputPath);
        Resize(inputStream, outputSize, outputPath, outputFormat, cropSquare, squareFrame);
    }

    public void Resize(Stream inputStream, Size outputSize, string outputPath, string outputFormat, bool cropSquare,
        Rectangle? squareFrame)
    {
        if (outputFormat.StartsWith("."))
            outputFormat = outputFormat.Substring(1);
        if (string.IsNullOrEmpty(outputFormat) || !_outputImageFormats.ContainsKey(outputFormat.ToLower()))
            throw new TTGException($"Image format {outputFormat} is not supported", code: "IMAGE_FORMAT_NOT_SUPPORTED");

        using (var output = File.OpenWrite(outputPath))
        {
            Resize(inputStream, outputSize, output, outputFormat, cropSquare, squareFrame);
        }

        if (!File.Exists(outputPath))
            throw new TTGException(
                $"Image resize failed. width: {outputSize.Width} height: {outputSize.Height} outputPath {outputPath}",
                code: "IMAGE_RESIZE_FAILED");
    }

    public void Resize(Stream inputStream, Size outputSize, Stream outputStream, string outputFormat, bool cropSquare,
        Rectangle? squareFrame)
    {
        if (outputFormat.StartsWith("."))
            outputFormat = outputFormat.Substring(1);
        if (string.IsNullOrEmpty(outputFormat) || !_outputImageFormats.ContainsKey(outputFormat.ToLower()))
            throw new TTGException($"Image format {outputFormat} is not supported", code: "IMAGE_FORMAT_NOT_SUPPORTED");

        SKEncodedImageFormat sKEncodedImageFormat = _outputImageFormats[outputFormat.ToLower()];

        int Quality = 100;

        using var managedStream = new SKManagedStream(inputStream);
        using var original = SKBitmap.Decode(managedStream);
        if (!cropSquare)
        {
            // resize the original image as is
            using var resized = original.Resize(new SKImageInfo(outputSize.Width, outputSize.Height),
                SKBitmapResizeMethod.Lanczos3);
            if (resized == null)
                throw new TTGException(
                    $"Image resize failed. width: {outputSize.Width} height: {outputSize.Height}",
                    code: "IMAGE_RESIZE_FAILED");

            using var image = SKImage.FromBitmap(resized);
            image.Encode(sKEncodedImageFormat, Quality)
                .SaveTo(outputStream);
        }
        else
        {
            bool ScaleX = original.Width >= original.Height ? true : false;
            SKBitmap squareBitmap = null;
            if (squareFrame != null)
            {
                //use the user provided frame to crop the square bitmap
                squareBitmap = new SKBitmap(squareFrame.Value.Width, squareFrame.Value.Height);
                original.ExtractSubset(squareBitmap, new SKRectI(
                    squareFrame.Value.X,
                    squareFrame.Value.Y,
                    squareFrame.Value.Right,
                    squareFrame.Value.Bottom));
            }
            else
            {
                //take the largest possible square from the center of the image, crop-off excess height/ width
                int diff = Math.Abs(original.Width - original.Height);
                int cropSize = diff / 2;
                int squareWidthHeight = ScaleX ? original.Width - diff : original.Height - diff;

                squareBitmap = new SKBitmap(squareWidthHeight, squareWidthHeight);

                if (ScaleX)
                    original.ExtractSubset(squareBitmap,
                        new SKRectI(cropSize, 0, original.Width - cropSize, original.Height));
                else
                    original.ExtractSubset(squareBitmap,
                        new SKRectI(0, cropSize, original.Width, original.Height - cropSize));
            }

            using var resized = squareBitmap.Resize(new SKImageInfo(outputSize.Width, outputSize.Height),
                SKBitmapResizeMethod.Lanczos3);
            if (resized == null)
                throw new TTGException(
                    $"Image resize failed. width: {outputSize.Width} height: {outputSize.Height}",
                    code: "IMAGE_RESIZE_FAILED");

            using var image = SKImage.FromBitmap(resized);
            image.Encode(sKEncodedImageFormat, Quality)
                .SaveTo(outputStream);
        }
    }
}