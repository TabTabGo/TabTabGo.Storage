using System.IO;
using System.Threading.Tasks;

namespace TabTabGo.Storage;

public abstract class StorageProvider : IStorageProvider
{
    public abstract Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown");
    public abstract Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown");
    public abstract Task<Stream> RetrieveAsync(string filePath, string container = "unknown");
    public abstract Task DeleteAsync(string filePath, string container = "unknown");

    public abstract Task<string> GetPublicPath(string filePath, string mediaType, string fileName, int expiryDays = 20,
        string container = "unknown");

    public abstract string GetRootDirectory(string container = "unknown");
}