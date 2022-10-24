using System.IO;
namespace TabTabGo.Storage.Services;

public interface IImageService
{
    void Resize(Stream inputStream, System.Drawing.Size outputSize, string outputPath, string outputFormat,
        bool cropSquare, System.Drawing.Rectangle? squareFrame);

    void Resize(Stream inputStream, System.Drawing.Size outputSize, Stream output, string outputFormat, bool cropSquare,
        System.Drawing.Rectangle? squareFrame);

    void Resize(string inputPath, System.Drawing.Size outputSize, string outputPath, string outputFormat,
        bool cropSquare, System.Drawing.Rectangle? squareFrame);
}