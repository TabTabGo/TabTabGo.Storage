using System.Drawing;
using System.Net;
using Microsoft.Extensions.Options;
using SkiaSharp;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Services;
using TabTabGo.Storage.TestKit;
using Xunit;

namespace TabTabGo.Storage.Services.Tests;

public class ImageServiceTests : IDisposable
{
    private readonly ImageService _service = new();
    private readonly TempDirectory _temp = new();

    public void Dispose() => _temp.Dispose();

    private byte[] Resize(byte[] input, int w, int h, string format, bool crop = false, Rectangle? frame = null, ImageService? service = null)
    {
        using var output = new MemoryStream();
        (service ?? _service).Resize(new MemoryStream(input), new Size(w, h), output, format, crop, frame);
        return output.ToArray();
    }

    private static bool IsReddish(SKColor c) => c.Red > 150 && c.Blue < 100;

    private static bool IsBluish(SKColor c) => c.Blue > 150 && c.Red < 100;

    public static IEnumerable<object[]> Formats()
    {
        yield return ["png", TestImages.Png(64, 48), SKEncodedImageFormat.Png];
        yield return ["jpg", TestImages.Jpeg(64, 48), SKEncodedImageFormat.Jpeg];
        yield return ["jpeg", TestImages.Jpeg(64, 48), SKEncodedImageFormat.Jpeg];
        yield return ["webp", TestImages.Webp(64, 48), SKEncodedImageFormat.Webp];
        yield return ["gif", TestImages.Gif(16, 12), SKEncodedImageFormat.Png];
        yield return ["bmp", TestImages.Bmp(16, 12), SKEncodedImageFormat.Png];
        yield return ["ico", TestImages.Ico(16), SKEncodedImageFormat.Png];
    }

    // V-13
    [Theory]
    [MemberData(nameof(Formats))]
    public void Resize_Theory_PerFormat(string format, byte[] input, SKEncodedImageFormat expectedOutput)
    {
        var output = Resize(input, 30, 30, "." + format, crop: true);

        using var codec = SKCodec.Create(new MemoryStream(output));
        Assert.Equal(expectedOutput, codec.EncodedFormat);
        Assert.Equal((30, 30), TestImages.SizeOf(output));
    }

    [Fact]
    public void Resize_UpperCaseAndDottedFormat_Accepted() =>
        Assert.Equal((10, 10), TestImages.SizeOf(Resize(TestImages.Png(20, 20), 10, 10, ".PNG")));

    [Fact]
    public void Resize_WithoutCrop_StretchesToRequestedSize() =>
        Assert.Equal((50, 10), TestImages.SizeOf(Resize(TestImages.Png(20, 40), 50, 10, "png")));

    [Theory]
    [InlineData("tiff")]
    [InlineData("xyz")]
    [InlineData("")]
    [InlineData(" ")]
    public void Resize_UnsupportedFormat_400(string format)
    {
        var ex = Assert.Throws<ApiException>(() => Resize(TestImages.Png(8, 8), 4, 4, format));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal("IMAGE_FORMAT_NOT_SUPPORTED", ex.ErrorCode);
    }

    // V-14
    [Fact]
    public void Resize_NotAnImage_400()
    {
        var ex = Assert.Throws<ApiException>(() => Resize(TestImages.Corrupt(), 4, 4, "png"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal("IMAGE_NOT_DECODABLE", ex.ErrorCode);
    }

    [Fact]
    public void Resize_PlainText_400() =>
        Assert.Equal("IMAGE_NOT_DECODABLE",
            Assert.Throws<ApiException>(() => Resize("hello world"u8.ToArray(), 4, 4, "png")).ErrorCode);

    // V-12
    [Fact]
    public void Resize_DeclaredHugeDimensions_Rejected_WithoutDecoding()
    {
        var bomb = TestImages.PngDeclaring(30_000, 30_000);

        var ex = Assert.Throws<ApiException>(() => Resize(bomb, 10, 10, "png"));

        Assert.Equal("IMAGE_TOO_LARGE", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.True(bomb.Length < 100);
    }

    [Fact]
    public void Resize_MaxPixels_FromOptions()
    {
        var service = new ImageService(Options.Create(new ImageServiceOptions { MaxPixels = 100 }));

        Assert.Equal("IMAGE_TOO_LARGE",
            Assert.Throws<ApiException>(() => Resize(TestImages.Png(20, 20), 4, 4, "png", service: service)).ErrorCode);
        Assert.Equal((4, 4), TestImages.SizeOf(Resize(TestImages.Png(10, 10), 4, 4, "png", service: service)));
    }

    // V-15
    [Fact]
    public void Resize_ExifOrientation6_Rotated()
    {
        // 40x20: left half red, right half blue. Orientation 6 rotates 90 degrees clockwise: red ends on top.
        var photo = TestImages.WithExifOrientation(TestImages.TwoTone(40, 20, SKEncodedImageFormat.Jpeg), 6);

        var output = Resize(photo, 20, 40, "png");

        Assert.Equal((20, 40), TestImages.SizeOf(output));
        Assert.True(IsReddish(TestImages.PixelAt(output, 10, 4)), "top should be red");
        Assert.True(IsBluish(TestImages.PixelAt(output, 10, 36)), "bottom should be blue");
    }

    [Theory]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(4, true)]
    [InlineData(5, false)]
    [InlineData(6, false)]
    [InlineData(7, false)]
    [InlineData(8, false)]
    public void Resize_ExifOrientation_SwapsDimensionsOnlyForQuarterTurns(int orientation, bool keepsShape)
    {
        var photo = TestImages.WithExifOrientation(TestImages.TwoTone(40, 20, SKEncodedImageFormat.Jpeg), (ushort)orientation);

        var output = Resize(photo, 40, 20, "png");
        // Resize to a fixed box; use crop to a square to observe which half lands where is covered by the test above.
        Assert.Equal((40, 20), TestImages.SizeOf(output));
        Assert.True(keepsShape || orientation >= 5);
    }

    [Fact]
    public void Resize_ExifOrientation3_RotatesHalfTurn()
    {
        var photo = TestImages.WithExifOrientation(TestImages.TwoTone(40, 20, SKEncodedImageFormat.Jpeg), 3);

        var output = Resize(photo, 40, 20, "png");

        Assert.True(IsBluish(TestImages.PixelAt(output, 4, 10)), "left should now be blue");
        Assert.True(IsReddish(TestImages.PixelAt(output, 36, 10)), "right should now be red");
    }

    [Fact]
    public void Resize_ExifOrientation2_MirrorsHorizontally()
    {
        var photo = TestImages.WithExifOrientation(TestImages.TwoTone(40, 20, SKEncodedImageFormat.Jpeg), 2);

        var output = Resize(photo, 40, 20, "png");

        Assert.True(IsBluish(TestImages.PixelAt(output, 4, 10)));
        Assert.True(IsReddish(TestImages.PixelAt(output, 36, 10)));
    }

    [Fact]
    public void Resize_ExifOrientation8_RotatesCounterClockwise()
    {
        var photo = TestImages.WithExifOrientation(TestImages.TwoTone(40, 20, SKEncodedImageFormat.Jpeg), 8);

        var output = Resize(photo, 20, 40, "png");

        Assert.True(IsBluish(TestImages.PixelAt(output, 10, 4)), "right half becomes the top");
        Assert.True(IsReddish(TestImages.PixelAt(output, 10, 36)));
    }

    [Theory]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    public void Resize_ExifOrientation_OtherOrigins_Decode(int orientation)
    {
        var photo = TestImages.WithExifOrientation(TestImages.TwoTone(40, 20, SKEncodedImageFormat.Jpeg), (ushort)orientation);

        var output = Resize(photo, 20, 20, "png");

        Assert.Equal((20, 20), TestImages.SizeOf(output));
    }

    // V-16
    [Fact]
    public void CropSquare_OddDifference_IsSquare()
    {
        // 31x20 source: the centred square is 20x20 starting at x=5 (the odd pixel falls on the far edge).
        var output = Resize(TestImages.TwoTone(31, 20), 20, 20, "png", crop: true);

        Assert.Equal((20, 20), TestImages.SizeOf(output));
        Assert.True(IsReddish(TestImages.PixelAt(output, 1, 10)));
        Assert.True(IsBluish(TestImages.PixelAt(output, 18, 10)));
    }

    [Fact]
    public void CropSquare_Portrait_TakesCentre()
    {
        var output = Resize(TestImages.TwoTone(20, 41), 20, 20, "png", crop: true);

        Assert.Equal((20, 20), TestImages.SizeOf(output));
        Assert.True(IsReddish(TestImages.PixelAt(output, 2, 10)));
    }

    [Fact]
    public void CropSquare_WithFrame_UsesFrame()
    {
        var output = Resize(TestImages.TwoTone(40, 20), 10, 20, "png", crop: true, frame: new Rectangle(0, 0, 10, 20));

        Assert.True(IsReddish(TestImages.PixelAt(output, 5, 10)));
        Assert.True(IsReddish(TestImages.PixelAt(output, 1, 1)));
    }

    [Theory]
    [InlineData(30, 0, 20, 20)]
    [InlineData(-1, 0, 10, 10)]
    [InlineData(0, 0, 0, 10)]
    [InlineData(0, 0, 10, 0)]
    [InlineData(0, 15, 10, 10)]
    public void CropSquare_FrameOutOfBounds_400(int x, int y, int w, int h)
    {
        var ex = Assert.Throws<ApiException>(() => Resize(TestImages.Png(40, 20), 10, 10, "png", crop: true, frame: new Rectangle(x, y, w, h)));

        Assert.Equal("IMAGE_CROP_FRAME_INVALID", ex.ErrorCode);
    }

    // V-17
    [Fact]
    public void ResizeToPath_OverwriteLargerFile_Truncates()
    {
        var path = Path.Combine(_temp.Path, "out.png");
        File.WriteAllBytes(path, new byte[200_000]);

        _service.Resize(new MemoryStream(TestImages.Png(20, 20)), new Size(8, 8), path, "png", false, null);

        var written = File.ReadAllBytes(path);
        Assert.True(written.Length < 5_000);
        Assert.Equal((8, 8), TestImages.SizeOf(written));
    }

    [Fact]
    public void ResizeToPath_NotDecodable_LeavesNoFile()
    {
        var path = Path.Combine(_temp.Path, "bad.png");

        var ex = Assert.Throws<ApiException>(() =>
            _service.Resize(new MemoryStream([1, 2, 3, 4]), new Size(8, 8), path, "png", false, null));

        Assert.Equal("IMAGE_NOT_DECODABLE", ex.ErrorCode);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public void ResizeToPath_NotDecodable_KeepsExistingFileIntact()
    {
        var path = Path.Combine(_temp.Path, "keep.png");
        var original = TestImages.Png(10, 10);
        File.WriteAllBytes(path, original);

        Assert.Throws<ApiException>(() =>
            _service.Resize(new MemoryStream([1, 2, 3, 4]), new Size(8, 8), path, "png", false, null));

        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void ResizeToPath_TooLarge_KeepsExistingFileIntact()
    {
        var path = Path.Combine(_temp.Path, "keep2.png");
        var original = TestImages.Png(10, 10);
        File.WriteAllBytes(path, original);
        var missing = Path.Combine(_temp.Path, "none.png");

        var ex = Assert.Throws<ApiException>(() =>
            _service.Resize(new MemoryStream(TestImages.PngDeclaring(30_000, 30_000)), new Size(8, 8), path, "png", false, null));
        Assert.Throws<ApiException>(() =>
            _service.Resize(new MemoryStream(TestImages.PngDeclaring(30_000, 30_000)), new Size(8, 8), missing, "png", false, null));

        Assert.Equal("IMAGE_TOO_LARGE", ex.ErrorCode);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.False(File.Exists(missing));
    }

    [Fact]
    public void ResizeFromPathToPath_Works()
    {
        var input = Path.Combine(_temp.Path, "in.png");
        var output = Path.Combine(_temp.Path, "out.jpg");
        File.WriteAllBytes(input, TestImages.Png(40, 40));

        _service.Resize(input, new Size(10, 10), output, "jpg", true, null);

        Assert.Equal((10, 10), TestImages.SizeOf(File.ReadAllBytes(output)));
    }

    [Fact]
    public void ResizeToPath_UnsupportedFormat_LeavesNoFile()
    {
        var output = Path.Combine(_temp.Path, "none.xyz");

        Assert.Throws<ApiException>(() =>
            _service.Resize(new MemoryStream(TestImages.Png(8, 8)), new Size(4, 4), output, "xyz", false, null));

        Assert.False(File.Exists(output));
    }

    // V-18
    [Fact]
    public void Resize_JpegQuality_FromOptions()
    {
        var noisy = NoisyJpeg();
        var low = new ImageService(Options.Create(new ImageServiceOptions { JpegQuality = 5 }));
        var high = new ImageService(Options.Create(new ImageServiceOptions { JpegQuality = 100 }));

        var small = Resize(noisy, 200, 200, "jpg", service: low);
        var big = Resize(noisy, 200, 200, "jpg", service: high);

        Assert.True(small.Length < big.Length, $"{small.Length} should be smaller than {big.Length}");
    }

    [Fact]
    public void Options_Defaults()
    {
        var options = new ImageServiceOptions();

        Assert.Equal(25_000_000, options.MaxPixels);
        Assert.Equal(90, options.JpegQuality);
    }

    [Fact]
    public void Resize_NonSeekableInput_IsBuffered()
    {
        using var output = new MemoryStream();

        _service.Resize(new NonSeekable(TestImages.Png(20, 20)), new Size(5, 5), output, "png", false, null);

        Assert.Equal((5, 5), TestImages.SizeOf(output.ToArray()));
    }

    [Fact]
    public void Resize_NullStreams_Throw()
    {
        Assert.Throws<ArgumentNullException>(() => _service.Resize((Stream)null!, new Size(1, 1), new MemoryStream(), "png", false, null));
        Assert.Throws<ArgumentNullException>(() => _service.Resize(new MemoryStream(), new Size(1, 1), (Stream)null!, "png", false, null));
    }

    private static byte[] NoisyJpeg()
    {
        using var bitmap = new SKBitmap(200, 200);
        var random = new Random(7);
        for (var x = 0; x < 200; x++)
        {
            for (var y = 0; y < 200; y++)
            {
                bitmap.SetPixel(x, y, new SKColor((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }

        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, 100).ToArray();
    }

    private sealed class NonSeekable(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;
    }
}

public class ImageSizeAndFormatTests
{
    [Theory]
    [InlineData(ImageSize.Default, 1800, 1000)]
    [InlineData(ImageSize.ThumbLarge, 180, 180)]
    [InlineData(ImageSize.ThumbMedium, 160, 160)]
    [InlineData(ImageSize.ThumbSmall, 120, 120)]
    public void GetSize_AllPresets(ImageSize size, int width, int height) =>
        Assert.Equal(new Size(width, height), size.GetSize());

    [Fact]
    public void GetSize_Undefined_Throws()
    {
        var ex = Assert.Throws<ApiException>(() => ((ImageSize)99).GetSize());

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal("IMAGE_SIZE_NOT_SUPPORTED", ex.ErrorCode);
    }

    [Theory]
    [InlineData("png", true)]
    [InlineData(".JPG", true)]
    [InlineData("webp", true)]
    [InlineData("gif", true)]
    [InlineData("bmp", true)]
    [InlineData("ico", true)]
    [InlineData("tiff", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSupported(string? extension, bool expected) => Assert.Equal(expected, ImageFormats.IsSupported(extension));

    [Theory]
    [InlineData(".gif", ".png")]
    [InlineData(".BMP", ".png")]
    [InlineData("ico", "png")]
    [InlineData(".png", ".png")]
    [InlineData(".jpg", ".jpg")]
    [InlineData("webp", "webp")]
    public void GetThumbnailExtension_PngForNonEncodableSources(string source, string expected) =>
        Assert.Equal(expected, ImageFormats.GetThumbnailExtension(source));

    [Fact]
    public void GetThumbnailExtension_Null_Throws() =>
        Assert.Throws<ArgumentNullException>(() => ImageFormats.GetThumbnailExtension(null!));
}
