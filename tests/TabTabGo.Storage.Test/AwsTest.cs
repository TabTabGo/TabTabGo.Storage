using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TabTabGo.Storage.Aws;
using Xunit;

namespace TabTabGo.Storage.Test;

public class AwsTest
{
    IConfiguration _config;
    private string fileId = $"{Guid.NewGuid().ToString()}.png";
    private ILogger<S3StorageProvider> _logger = new LoggerFactory().CreateLogger<S3StorageProvider>();
    public AwsTest()
    {
        // configure aws
         _config = new ConfigurationBuilder()
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
            .AddJsonFile("appsettings.Development.json", optional: true, reloadOnChange: true)
            .AddEnvironmentVariables()
            .AddUserSecrets<AwsTest>()
            .Build();
    }
    IOptions<S3Configuration> GetConfig()
    {
        var config = _config.GetSection("Aws");
        var awsConfig = new S3Configuration();
        config.Bind(awsConfig);
        return Options.Create(awsConfig);
    }
    
    [Fact]
    [Trait("Priority","01")]
    public async Task AddToStorage_File_Available()
    {
        // get aws config with option S3Configuration
        var storageProvider = new S3StorageProvider(GetConfig(), _logger);
        var fileBytes = await File.ReadAllBytesAsync("test.png");
        
        await storageProvider.StoreAsync(fileId, fileBytes);

        var savedFileStream = await storageProvider.RetrieveAsync(fileId);

        var saveFile = (savedFileStream as MemoryStream).ToArray();
        Assert.Equal(fileBytes, saveFile);
        await savedFileStream.DisposeAsync();
    }
    
    [Fact]
    [Trait("Priority","02")]
    public async Task Delete_Stored_File()
    {
        var storageProvider = new S3StorageProvider(GetConfig(),_logger);
        await storageProvider.DeleteAsync(fileId);
        var savedFileStream = await storageProvider.RetrieveAsync(fileId);
        Assert.Null(savedFileStream);
    }
}