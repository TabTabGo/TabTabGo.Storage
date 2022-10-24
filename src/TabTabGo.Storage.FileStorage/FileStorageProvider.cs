using System;
using System.IO;
using System.Threading.Tasks;
using TabTabGo.Storage.Services;

namespace TabTabGo.Storage.FileStorage;

public class FileStorageProvider : StorageProvider
{
    private readonly IPathService _pathService;

    public FileStorageProvider(IPathService pathService)
    {
        _pathService = pathService;
    }

    public override string GetRootDirectory(string container = "unknown")
    {
        string destinationRootDirectory = _pathService.PathSettings[container] ?? string.Empty;

        return Path.Combine(_pathService.PathSettings.RootPath, destinationRootDirectory);
    }

    public override async Task<string> StoreAsync(byte[] buffer, string extension = ".tmp",
        string container = "unknown")
    {
        string randomFileName = null;
        string defaultFileFullPath = null;
        string filePath = null;

        string dateTimeFolder = DateTime.Now.ToString(_pathService.PathSettings.DateFormat ?? "yyyyMMdd");
        string destinationDirectory = GetRootDirectory(container);

        if (!Directory.Exists(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        if (extension.StartsWith("."))
            extension = extension.Substring(1);
        do
        {
            randomFileName = Path.GetFileNameWithoutExtension(Path.GetRandomFileName());
            filePath = Path.Combine(dateTimeFolder, $@"{randomFileName}.{extension}");
            defaultFileFullPath = Path.Combine(destinationDirectory, filePath);
        } while (File.Exists(defaultFileFullPath));

        return await StoreAsync(filePath, buffer, container);
    }

    public override async Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown")
    {
        string destinationDirectory = GetRootDirectory(container);

        if (!Directory.Exists(destinationDirectory))
            Directory.CreateDirectory(destinationDirectory);

        string defaultFileFullPath = Path.Combine(destinationDirectory, filePath);
        if (File.Exists(defaultFileFullPath))
        {
            // TODO handle duplicate files
        }

        //await FileUtility.WriteFileContentAsync(buffer, defaultFileFullPath, true);
        // write file to disk
        await using var fileStream = new FileStream(defaultFileFullPath, FileMode.Create, FileAccess.Write);
        await fileStream.WriteAsync(buffer, 0, buffer.Length);
        return filePath;
    }

    public override async Task<Stream> RetrieveAsync(string filePath, string container = "unknown")
    {
        string fileName = Path.GetFileName(filePath);
        string directoryName = Path.GetDirectoryName(filePath);
        string destinationRootDirectory = _pathService.PathSettings[container] ?? string.Empty;
        string fullVirtualPath = Path.Combine(_pathService.PathSettings.RootPath, destinationRootDirectory,
            directoryName, fileName);
        string fullPhysicalPath = _pathService.ResolvePath(fullVirtualPath);
        // load data from disk into MemoryStream
        var memoryStream = new MemoryStream();
        await using var fileStream = new FileStream(fullPhysicalPath, FileMode.Open, FileAccess.Read);
        await fileStream.CopyToAsync(memoryStream);
        memoryStream.Position = 0;
        return memoryStream;
    }

    public override async Task DeleteAsync(string filePath, string container = "unknown")
    {
        string fileName = Path.GetFileName(filePath);
        string directoryName = Path.GetDirectoryName(filePath);
        string destinationRootDirectory = _pathService.PathSettings[container] ?? string.Empty;
        string fullVirtualPath = Path.Combine(_pathService.PathSettings.RootPath, destinationRootDirectory,
            directoryName, fileName);
        string fullPhysicalPath = _pathService.ResolvePath(fullVirtualPath);
        if (File.Exists(fullPhysicalPath))
        {
            // delete file from disk
            await Task.Run(() => File.Delete(fullPhysicalPath));
        }
    }

    public override Task<string> GetPublicPath(string filePath, string mediaType, string fileName, int expiryDays = 20,
        string container = "unknown")
    {
        return Task.Run(() => filePath);
    }
}