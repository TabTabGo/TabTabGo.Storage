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
            _config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddUserSecrets<AzureBlobTests>()
                .Build();
        }
        [Fact]
        public async Task AddToStorage()
        {
            var storageProvider = new BlobStorageProvider(_config);
            var fileBytes = await File.ReadAllBytesAsync("test.png");
            var fileId = await storageProvider.StoreAsync(fileBytes, ".png","test");

            var savedFileStream = await storageProvider.RetrieveAsync(fileId, "test");
            var ms = new MemoryStream();
            await savedFileStream.CopyToAsync(ms);

            var saveFile = ms.ToArray();
            Assert.Equal(fileBytes, saveFile);
        }
    }
}
