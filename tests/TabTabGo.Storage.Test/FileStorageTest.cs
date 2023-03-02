using System.IO;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Moq;
using TabTabGo.Storage.Aws;
using TabTabGo.Storage.FileStorage;
using TabTabGo.Storage.FileStorage.Services;
using TabTabGo.Storage.Services;
using Xunit;

namespace TabTabGo.Storage.Test;

public class FileStorageTest
{
    private ILogger<PathService> _logger = new LoggerFactory().CreateLogger<PathService>();
    private readonly PathService _pathService;
    private readonly FileStorageProvider _fileStorageProvider;
    public FileStorageTest()
    {
        var environment = new Mock<IHostingEnvironment>();
        environment.SetupGet(e => e.ContentRootPath).Returns(Directory.GetCurrentDirectory());
        environment.SetupGet(e => e.WebRootPath).Returns(Directory.GetCurrentDirectory());
        
        // get configuration from appsettings.json
        var configuration = new ConfigurationBuilder()
            .SetBasePath(Directory.GetCurrentDirectory())
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .Build();
        var pathSettings = new PathSettings();
        configuration.GetSection("PathSettings").Bind(pathSettings);
        
        _pathService = new PathService(environment.Object, Options.Create<PathSettings>(pathSettings), _logger);
        _fileStorageProvider = new FileStorageProvider(_pathService);
        
    }
    
    [Fact]
    [Trait("Priority","01")]
    public async Task AddToStorage_File_Available()
    {
        // get aws config with option S3Configuration
        var fileBytes = await File.ReadAllBytesAsync("test.png");
        
       var fileId = await _fileStorageProvider.StoreAsync(fileBytes,".png");

        var savedFileStream = await _fileStorageProvider.RetrieveAsync(fileId);

        var saveFile = (savedFileStream as MemoryStream).ToArray();
        Assert.Equal(fileBytes, saveFile);
        await savedFileStream.DisposeAsync();
    }
}