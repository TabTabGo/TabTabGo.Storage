using System;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace TabTabGo.Storage.AzureStorage;

public class BlobStorageProvider : StorageProvider
{
    private readonly IConfiguration _configuration;
    private readonly ILogger<BlobStorageProvider> _logger;
    public BlobStorageProvider(IConfiguration configuration, ILogger<BlobStorageProvider> logger)
    {
        _configuration = configuration;
        _logger = logger;
    }

    public override async Task<string> StoreAsync(byte[] buffer, string extension = ".tmp",
        string container = "unknown")
    {
        if (extension.StartsWith("."))
            extension = extension.Substring(1);

        var fileIdentifier = $"{Guid.NewGuid().ToString()}.{extension}";

        await StoreAsync(fileIdentifier, buffer, container);

        return fileIdentifier;
    }

    public override async Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown")
    {
        var blockBlob = await GetBlob(filePath, container);
        _logger.LogTrace("Uploading blob {0} under container {1}", blockBlob.Name, blockBlob.BlobContainerName);
        using var memoryStream = new MemoryStream();
        await memoryStream.WriteAsync(buffer, 0, buffer.Length);
        memoryStream.Seek(0, SeekOrigin.Begin);
        
        var response = await blockBlob.UploadAsync(memoryStream);
         _logger.LogTrace("Blob {0} under container {1} size {2} bytes uploaded with Sequance number: {3} , version no : {4}", blockBlob.Name, container, buffer.Length.ToString() , response.Value.BlobSequenceNumber.ToString(), response.Value.VersionId);
        return filePath;
    }

    public override string GetRootDirectory(string container = "unknown")
    {
        return string.Empty;
    }

    public override async Task<Stream> RetrieveAsync(string fileIdentifier, string container = "unknown")
    {
        var blockBlob = await GetBlob(fileIdentifier, container);
        _logger.LogTrace("Retrieving blob {0} under container {1}", blockBlob.Name, blockBlob.BlobContainerName);
        
        var memoryStream = new MemoryStream();

        // Download the blob's contents and save it to a file
        BlobDownloadInfo download = await blockBlob.DownloadAsync();
        _logger.LogTrace("Blob {0} under container {1} of type {2} and size {3} bytes retrieved", fileIdentifier, container, download.BlobType.ToString(), download.ContentLength.ToString());
        await download.Content.CopyToAsync(memoryStream);

        memoryStream.Seek(0, SeekOrigin.Begin);

        return memoryStream;
    }

    public override async Task DeleteAsync(string fileIdentifier, string container = "unknown")
    {
        var blockBlob = await GetBlob(fileIdentifier, container);
        _logger.LogTrace("Deleting blob {0} under container {1}", fileIdentifier, container);
        if (await blockBlob.ExistsAsync())
        {
            await blockBlob.DeleteAsync();
            _logger.LogTrace("Blob {0} under container {1} deleted", fileIdentifier, container);
        }
        else
        {
            _logger.LogWarning("Blob {0} under container {1} not found", fileIdentifier, container);
        }
    }

    public override async Task<string> GetPublicPath(string fileIdentifier, string mediaType, string fileName,
        int expiryDays = 20, string container = "unknown")
    {
        var blobClient = await GetBlob(fileIdentifier, container);

        // Check whether this BlobClient object has been authorized with Shared Key.
        if (blobClient.CanGenerateSasUri)
        {
            // Create a SAS token that's valid for one hour.
            BlobSasBuilder sasBuilder = new BlobSasBuilder
            {
                BlobContainerName = blobClient.GetParentBlobContainerClient().Name,
                BlobName = blobClient.Name,
                Resource = "b",
                ExpiresOn = DateTimeOffset.UtcNow.AddHours(1)
            };

            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            Uri sasUri = blobClient.GenerateSasUri(sasBuilder);
            Console.WriteLine("SAS URI for blob is: {0}", sasUri);
            Console.WriteLine();

            return sasUri.AbsolutePath;
        }
        else
        {
            Console.WriteLine(@"BlobClient must be authorized with Shared Key 
                          credentials to create a service SAS.");
            return null;
        }
    }

    private async Task<BlobClient> GetBlob(string fileIdentifier, string containerName)
    {
        fileIdentifier = fileIdentifier.Replace("\\", "/").ToLower();
        // Retrieve storage account from connection string.
        if (_configuration.GetConnectionString("AzureStorageConnection") == null)
        {
            throw new Exception($"Configuration {"AzureStorageConnection"} is missing from the config file");
        }

        _logger.LogTrace("Initialize Blob service ");
        // Create a blobServiceClient object which will be used to create a container client
        var blobServiceClient = new BlobServiceClient(_configuration.GetConnectionString("AzureStorageConnection"));
        containerName = ConvertToValidDnsName(containerName);
        // get azure container
        // Create the container and return a container client object
        var containerClient = blobServiceClient.GetBlobContainerClient(containerName) ?? await blobServiceClient.CreateBlobContainerAsync(containerName);
        _logger.LogTrace("Get Blob container {0}", containerName);
        // Create the container if it doesn't already exist.
        await containerClient.CreateIfNotExistsAsync();
        _logger.LogTrace("Successfully create container {1} if nor exist", containerName);
        // Retrieve reference to a blob
        return containerClient.GetBlobClient(fileIdentifier);
        
    }
    
    private static string ConvertToValidDnsName(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            throw new ArgumentException("input cannot be null or empty");
        }

        // trim white spaces, convert to lowercase
        input = input.Trim().ToLower();

        // replace spaces with hyphens
        input = input.Replace(" ", "-");

        // remove invalid characters
        input = Regex.Replace(input, @"[^a-z0-9\-]", "");

        // truncate if necessary to meet DNS max length requirement
        if (input.Length > 63)
        {
            input = input.Substring(0, 63);
        }

        // ensure the name doesn't start or end with a hyphen
        input = input.Trim('-');

        if (input.Length < 1)
        {
            throw new ArgumentException("Resulting string is empty");
        }

        return input;
    }

}