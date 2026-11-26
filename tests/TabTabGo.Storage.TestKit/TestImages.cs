using System.Buffers.Binary;
using System.IO.Hashing;
using SkiaSharp;

namespace TabTabGo.Storage.TestKit;

/// <summary>Generates small test images in memory, so no binary fixtures are committed.</summary>
public static class TestImages
{
    /// <summary>A solid-colour image encoded with Skia (PNG, JPEG or WEBP).</summary>
    public static byte[] Encoded(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = Bitmap(width, height);
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 90);
        return data.ToArray();
    }

    /// <summary>A PNG.</summary>
    public static byte[] Png(int width, int height) => Encoded(width, height);

    /// <summary>A JPEG.</summary>
    public static byte[] Jpeg(int width, int height) => Encoded(width, height, SKEncodedImageFormat.Jpeg);

    /// <summary>A WEBP.</summary>
    public static byte[] Webp(int width, int height) => Encoded(width, height, SKEncodedImageFormat.Webp);

    /// <summary>A 24-bit BMP written by hand (Skia cannot encode BMP).</summary>
    public static byte[] Bmp(int width, int height)
    {
        var rowSize = (width * 3 + 3) / 4 * 4;
        var pixelBytes = rowSize * height;
        var bytes = new byte[54 + pixelBytes];
        bytes[0] = (byte)'B';
        bytes[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(2), bytes.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(10), 54);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(14), 40);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(18), width);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(22), height);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(26), 1);
        BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(28), 24);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(34), pixelBytes);
        for (var i = 54; i < bytes.Length; i++)
        {
            bytes[i] = (byte)(i % 251);
        }

        return bytes;
    }

    /// <summary>A GIF written by hand (Skia cannot encode GIF). LZW codes are emitted uncompressed.</summary>
    public static byte[] Gif(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write("GIF89a"u8);
        Span<byte> header = stackalloc byte[7];
        BinaryPrimitives.WriteUInt16LittleEndian(header, (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(header[2..], (ushort)height);
        header[4] = 0xF7; // global colour table, 256 entries
        output.Write(header);
        for (var i = 0; i < 256; i++)
        {
            output.WriteByte((byte)i);
            output.WriteByte((byte)(255 - i));
            output.WriteByte((byte)(i / 2));
        }

        output.WriteByte(0x2C);
        Span<byte> descriptor = stackalloc byte[9];
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[4..], (ushort)width);
        BinaryPrimitives.WriteUInt16LittleEndian(descriptor[6..], (ushort)height);
        output.Write(descriptor);
        output.WriteByte(8); // minimum LZW code size

        var packed = PackGifCodes(width * height);
        for (var offset = 0; offset < packed.Length; offset += 255)
        {
            var count = Math.Min(255, packed.Length - offset);
            output.WriteByte((byte)count);
            output.Write(packed, offset, count);
        }

        output.WriteByte(0);
        output.WriteByte(0x3B);
        return output.ToArray();
    }

    /// <summary>An ICO that wraps a PNG image.</summary>
    public static byte[] Ico(int size)
    {
        var png = Png(size, size);
        using var output = new MemoryStream();
        Span<byte> dir = stackalloc byte[22];
        BinaryPrimitives.WriteUInt16LittleEndian(dir[2..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dir[4..], 1);
        dir[6] = (byte)size;
        dir[7] = (byte)size;
        BinaryPrimitives.WriteUInt16LittleEndian(dir[10..], 1);
        BinaryPrimitives.WriteUInt16LittleEndian(dir[12..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(dir[14..], png.Length);
        BinaryPrimitives.WriteInt32LittleEndian(dir[18..], 22);
        output.Write(dir);
        output.Write(png);
        return output.ToArray();
    }

    /// <summary>Returns a JPEG with an EXIF APP1 segment carrying the given orientation (1-8).</summary>
    public static byte[] WithExifOrientation(byte[] jpeg, ushort orientation)
    {
        // Big-endian TIFF with a single IFD0 entry: tag 0x0112 (Orientation), type SHORT, count 1.
        byte[] tiff =
        [
            0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08,
            0x00, 0x01,
            0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00,
        ];
        byte[] exifHeader = [0x45, 0x78, 0x69, 0x66, 0x00, 0x00];
        var length = 2 + exifHeader.Length + tiff.Length;
        using var output = new MemoryStream();
        output.Write(jpeg, 0, 2); // SOI
        output.WriteByte(0xFF);
        output.WriteByte(0xE1);
        output.WriteByte((byte)(length >> 8));
        output.WriteByte((byte)length);
        output.Write(exifHeader);
        output.Write(tiff);
        output.Write(jpeg, 2, jpeg.Length - 2);
        return output.ToArray();
    }

    /// <summary>A tiny PNG whose header declares the given dimensions (no pixel data).</summary>
    public static byte[] PngDeclaring(int width, int height)
    {
        using var output = new MemoryStream();
        output.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteInt32BigEndian(ihdr, width);
        BinaryPrimitives.WriteInt32BigEndian(ihdr[4..], height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(output, "IHDR"u8, ihdr);
        // A minimal (truncated-to-nothing) zlib stream so the header parses like a real PNG.
        WriteChunk(output, "IDAT"u8, [0x78, 0x9C, 0x63, 0x00, 0x00, 0x00, 0x02, 0x00, 0x01]);
        WriteChunk(output, "IEND"u8, []);
        return output.ToArray();
    }

    /// <summary>Bytes that start like a PNG but are not decodable.</summary>
    public static byte[] Corrupt() =>
        [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05];

    /// <summary>Reads a bitmap back and returns its size.</summary>
    public static (int Width, int Height) SizeOf(byte[] image)
    {
        using var bitmap = SKBitmap.Decode(image);
        return (bitmap.Width, bitmap.Height);
    }

    /// <summary>Colour of one pixel of a decoded image.</summary>
    public static SKColor PixelAt(byte[] image, int x, int y)
    {
        using var bitmap = SKBitmap.Decode(image);
        return bitmap.GetPixel(x, y);
    }

    /// <summary>A two-colour image: left half red, right half blue. Useful to see rotations and crops.</summary>
    public static byte[] TwoTone(int width, int height, SKEncodedImageFormat format = SKEncodedImageFormat.Png)
    {
        using var bitmap = new SKBitmap(width, height);
        using (var canvas = new SKCanvas(bitmap))
        {
            canvas.Clear(SKColors.Blue);
            using var paint = new SKPaint { Color = SKColors.Red };
            canvas.DrawRect(0, 0, width / 2f, height, paint);
        }

        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 100);
        return data.ToArray();
    }

    private static SKBitmap Bitmap(int width, int height)
    {
        var bitmap = new SKBitmap(width, height);
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(new SKColor(30, 120, 200));
        return bitmap;
    }

    private static void WriteChunk(Stream output, ReadOnlySpan<byte> type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        output.Write(length);
        output.Write(type);
        output.Write(data);
        var crc = new Crc32();
        crc.Append(type);
        crc.Append(data);
        Span<byte> checksum = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(checksum, BinaryPrimitives.ReadUInt32LittleEndian(crc.GetCurrentHash()));
        output.Write(checksum);
    }

    // Every pixel is a literal 9-bit code, with a clear code every 250 codes so the code width never grows.
    private static byte[] PackGifCodes(int pixelCount)
    {
        var bytes = new List<byte>();
        var bitBuffer = 0;
        var bitCount = 0;

        void Emit(int code)
        {
            bitBuffer |= code << bitCount;
            bitCount += 9;
            while (bitCount >= 8)
            {
                bytes.Add((byte)(bitBuffer & 0xFF));
                bitBuffer >>= 8;
                bitCount -= 8;
            }
        }

        var sinceClear = 0;
        Emit(256);
        for (var i = 0; i < pixelCount; i++)
        {
            if (sinceClear == 250)
            {
                Emit(256);
                sinceClear = 0;
            }

            Emit(i % 200);
            sinceClear++;
        }

        Emit(257);
        if (bitCount > 0)
        {
            bytes.Add((byte)(bitBuffer & 0xFF));
        }

        return bytes.ToArray();
    }
}
