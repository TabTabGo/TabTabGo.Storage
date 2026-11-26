namespace TabTabGo.Storage.Services;

/// <summary>Port for image resizing and cropping.</summary>
public interface IImageService
{
    /// <summary>Resizes an image stream and writes the result to a file.</summary>
    /// <param name="inputStream">Source image.</param>
    /// <param name="outputSize">Target size.</param>
    /// <param name="outputPath">Destination file; overwritten and truncated.</param>
    /// <param name="outputFormat">Output format (extension, for example <c>png</c>).</param>
    /// <param name="cropSquare">Crop a square before resizing.</param>
    /// <param name="squareFrame">Explicit crop frame; centre square when <c>null</c>.</param>
    void Resize(Stream inputStream, System.Drawing.Size outputSize, string outputPath, string outputFormat,
        bool cropSquare, System.Drawing.Rectangle? squareFrame);

    /// <summary>Resizes an image stream and writes the result to a stream.</summary>
    /// <param name="inputStream">Source image.</param>
    /// <param name="outputSize">Target size.</param>
    /// <param name="output">Destination stream.</param>
    /// <param name="outputFormat">Output format (extension).</param>
    /// <param name="cropSquare">Crop a square before resizing.</param>
    /// <param name="squareFrame">Explicit crop frame; centre square when <c>null</c>.</param>
    void Resize(Stream inputStream, System.Drawing.Size outputSize, Stream output, string outputFormat, bool cropSquare,
        System.Drawing.Rectangle? squareFrame);

    /// <summary>Resizes an image file and writes the result to a file.</summary>
    /// <param name="inputPath">Source image file.</param>
    /// <param name="outputSize">Target size.</param>
    /// <param name="outputPath">Destination file; overwritten and truncated.</param>
    /// <param name="outputFormat">Output format (extension).</param>
    /// <param name="cropSquare">Crop a square before resizing.</param>
    /// <param name="squareFrame">Explicit crop frame; centre square when <c>null</c>.</param>
    void Resize(string inputPath, System.Drawing.Size outputSize, string outputPath, string outputFormat,
        bool cropSquare, System.Drawing.Rectangle? squareFrame);
}
