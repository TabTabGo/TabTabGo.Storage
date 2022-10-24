using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using TabTabGo.Storage.AzureStorage;

using Xunit;

namespace TabTabGo.Storage.Test
{
    public class AzureBlobTests
    {
        IConfiguration _config;
        public AzureBlobTests()
        {
            
        }
        [Fact]
        public async Task AddToStorage()
        {
            var storageProvider = new BlobStorageProvider(_config);
            var fileBytes = await File.ReadAllBytesAsync("test.png");
            var fileId = await storageProvider.StoreAsync(fileBytes, ".png");

            var savedFileStream = await storageProvider.RetrieveAsync(fileId);
            var ms = new MemoryStream();
            await savedFileStream.CopyToAsync(ms);

            var saveFile = ms.ToArray();
            Assert.Equal(fileBytes, saveFile);
        }
    }
}
