using Microsoft.Extensions.Options;
using Amazon;
using Amazon.S3;
using Amazon.S3.Model;

namespace TabTabGo.Storage.Aws;

public class S3StorageProvider : StorageProvider
{
    private S3Configuration _configuration;
    private readonly AmazonS3Client _amazonS3Client;
    private readonly ILogger<S3StorageProvider> _logger;
    public S3StorageProvider(IOptions<S3Configuration> configuration, ILogger<S3StorageProvider> logger)
    {
        _logger = logger;
        _configuration = configuration.Value;
        var region = RegionEndpoint.GetBySystemName(_configuration.Region);
        _amazonS3Client = new(_configuration.AccessKey, _configuration.SecretKey, region);
    }

    public void SetConfiguration(S3Configuration configuration)
    {
        _configuration = configuration;
    }

    public override string GetRootDirectory(string container = "unknown")
    {
        return $"{container}/";
    }

    public override Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown")
    {
        if (extension.StartsWith("."))
            extension = extension.Substring(1);

        var fileIdentifier = $"{Guid.NewGuid().ToString()}.{extension}";

        return StoreAsync(fileIdentifier, buffer, container);
    }

    public override async Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown")
    {
        try
        {
            // Upload the file to S3
            var request = new PutObjectRequest
            {
                BucketName = _configuration.BucketName,
                Key = $"{container}/{filePath}",
                InputStream = new MemoryStream(buffer)
            };

            await _amazonS3Client.PutObjectAsync(request);
            return filePath;
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error uploading file to S3");
            return null;
        }
        
    }

    public override async Task<Stream> RetrieveAsync(string filePath, string container = "unknown")
    {
        // Get the file from S3
        var request = new GetObjectRequest
        {
            BucketName = _configuration.BucketName,
            Key = $"{container}/{filePath}",
        };
        try
        {
            using var response = await _amazonS3Client.GetObjectAsync(request);
            // Read the content of the file into a byte array
            var memoryStream = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memoryStream);
            return memoryStream;
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Error retrieving file from S3");
            return null;
        }
        
    }

    public override async Task DeleteAsync(string filePath, string container = "unknown")
    {
        try
        {
            // Delete the file from S3
            var request = new DeleteObjectRequest
            {
                BucketName = _configuration.BucketName,
                Key = $"{container}/{filePath}",
            };

            await _amazonS3Client.DeleteObjectAsync(request);
        }
        catch (Exception e)
        {
            _logger.LogError(e, "Error deleting file from S3");
        }
       
    }

    public override async Task<string> GetPublicPath(string filePath, string mediaType, string fileName,
        int expiryDays = 20,
        string container = "unknown")
    {
        try
        {
            // Create a request to get the public URL for the file
            var request = new GetPreSignedUrlRequest
            {
                BucketName = _configuration.BucketName,
                Key = $"{container}/{filePath}",
                Expires = DateTime.UtcNow.AddMinutes(_configuration.ExpirationMinutes),
                Protocol = Protocol.HTTPS
            };

            // Get the public URL for the file
            return _amazonS3Client.GetPreSignedURL(request);
        }
        catch (Exception e)
        {
           _logger.LogError(e, "Error getting public path for file from S3");
           return null;
        }
       
    }
}