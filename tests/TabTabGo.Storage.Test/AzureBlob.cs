using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using TabTabGo.Storage.AzureStorage;
using Microsoft.Extensions.Logging.Console;

using Xunit;

namespace TabTabGo.Storage.Test
{
    public class AzureBlobTests
    {
        
        IConfiguration _config;
        private ILogger<BlobStorageProvider> _logger;
        public AzureBlobTests()
        {
            _config = new ConfigurationBuilder()
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .AddUserSecrets<AzureBlobTests>()
                .Build();
            
            // configure logger to use console
            var loggerFactory = LoggerFactory.Create(builder => builder.AddConsole());
            _logger = loggerFactory.CreateLogger<BlobStorageProvider>();
            
        }
        [Fact]
        public async Task AddToStorage()
        {
            var storageProvider = new BlobStorageProvider(_config, _logger);
            var fileBytes = await File.ReadAllBytesAsync("test.png");
            var fileId = await storageProvider.StoreAsync(fileBytes, ".png","Test");

            var savedFileStream = await storageProvider.RetrieveAsync(fileId, "Test");
            var ms = new MemoryStream();
            await savedFileStream.CopyToAsync(ms);

            var saveFile = ms.ToArray();
            Assert.Equal(fileBytes, saveFile);
        }
    }
}
