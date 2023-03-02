using System.IO;
using System.Threading.Tasks;

namespace TabTabGo.Storage;

public interface IStorageProvider
{
    /// <summary>
    /// Get default root directory for the storage provider
    /// </summary>
    /// <param name="container"></param>
    /// <returns></returns>
    string GetRootDirectory(string container = "unknown");
    /// <summary>
    /// Store a new file in the storage provider
    /// </summary>
    /// <param name="buffer">array of data in bytes</param>
    /// <param name="extension">file extenstion</param>
    /// <param name="container">override name of directory</param>
    /// <returns>file path</returns>
    Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown");
    Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown");
    Task<Stream> RetrieveAsync(string filePath, string container = "unknown");
    Task DeleteAsync(string filePath, string container = "unknown");

    Task<string> GetPublicPath(string filePath, string mediaType, string fileName, int expiryDays = 20,
        string container = "unknown");
}