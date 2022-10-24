using System.IO;
using System.Threading.Tasks;

namespace TabTabGo.Storage;

public interface IStorageProvider
{
    string GetRootDirectory(string container = "unknown");
    Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown");
    Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown");
    Task<Stream> RetrieveAsync(string filePath, string container = "unknown");
    Task DeleteAsync(string filePath, string container = "unknown");

    Task<string> GetPublicPath(string filePath, string mediaType, string fileName, int expiryDays = 20,
        string container = "unknown");
}