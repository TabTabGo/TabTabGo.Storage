using System;
using System.IO;

using System.Threading.Tasks;

using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;

namespace TabTabGo.Storage.AzureStorage;

public class BlobStorageProvider : StorageProvider
{
    private readonly IConfiguration _configuration;

    public BlobStorageProvider(IConfiguration configuration)
    {
        _configuration = configuration;
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
        using var memoryStream = new MemoryStream();
        await memoryStream.WriteAsync(buffer, 0, buffer.Length);
        await blockBlob.UploadAsync(memoryStream);

        return filePath;
    }

    public override string GetRootDirectory(string container = "unknown")
    {
        return string.Empty;
    }

    public override async Task<Stream> RetrieveAsync(string fileIdentifier, string container = "unknown")
    {
        var blockBlob = await GetBlob(fileIdentifier, container);

        var memoryStream = new MemoryStream();

        // Download the blob's contents and save it to a file
        BlobDownloadInfo download = await blockBlob.DownloadAsync();

        await download.Content.CopyToAsync(memoryStream);

        memoryStream.Seek(0, SeekOrigin.Begin);

        return memoryStream;
    }

    public override async Task DeleteAsync(string fileIdentifier, string container = "unknown")
    {
        var blockBlob = await GetBlob(fileIdentifier, container);

        if (await blockBlob.ExistsAsync())
        {
            await blockBlob.DeleteAsync();
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

        // Create a blobServiceClient object which will be used to create a container client
        var blobServiceClient = new BlobServiceClient(_configuration.GetConnectionString("AzureStorageConnection"));


        // Create the container and return a container client object
        BlobContainerClient containerClient = await blobServiceClient.CreateBlobContainerAsync(containerName);

        // Create the container if it doesn't already exist.
        await containerClient.CreateIfNotExistsAsync();

        return containerClient.GetBlobClient(fileIdentifier);
        // Retrieve reference to a blob
        //return containerClient.GetBlockBlobReference(fileIdentifier);
    }
}