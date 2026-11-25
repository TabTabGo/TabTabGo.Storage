using System.Collections.Concurrent;
using System.Net;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Azure.Storage.Blobs.Specialized;
using Azure.Storage.Sas;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TabTabGo.Storage.ContentTypes;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;

namespace TabTabGo.Storage.AzureStorage;

/// <summary>
/// Azure Blob Storage implementation of <see cref="IStorageProvider"/>. A container maps to a blob container
/// (name normalised per Azure rules). The injected <see cref="BlobServiceClient"/> is shared; containers are created
/// only on write, once per process.
/// </summary>
public class BlobStorageProvider : StorageProvider
{
    private const string ProviderName = "AzureBlob";

    private readonly BlobServiceClient _serviceClient;
    private readonly AzureBlobStorageOptions _options;
    private readonly ILogger<BlobStorageProvider> _logger;
    private readonly ConcurrentDictionary<string, Task> _createdContainers = new(StringComparer.Ordinal);

    /// <summary>Creates the provider around a shared client.</summary>
    /// <param name="serviceClient">Shared blob service client.</param>
    /// <param name="options">Provider options.</param>
    /// <param name="logger">Logger. SAS URLs and keys are never logged.</param>
    public BlobStorageProvider(BlobServiceClient serviceClient, IOptions<AzureBlobStorageOptions> options,
        ILogger<BlobStorageProvider> logger)
    {
        _serviceClient = serviceClient ?? throw new ArgumentNullException(nameof(serviceClient));
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Legacy constructor: reads <c>ConnectionStrings:AzureStorageConnection</c> and builds its own client.</summary>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="logger">Logger.</param>
    [Obsolete("Use AddTabTabGoAzureBlobStorage() or the (BlobServiceClient, IOptions<AzureBlobStorageOptions>, ILogger) constructor.")]
    public BlobStorageProvider(IConfiguration configuration, ILogger<BlobStorageProvider> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _options = new AzureBlobStorageOptions
        {
            ConnectionString = configuration.GetConnectionString(AzureBlobStorageOptions.LegacyConnectionStringName),
        };
        _serviceClient = AzureBlobClientFactory.Create(_options);
    }

    /// <inheritdoc />
    public override string GetRootDirectory(string container = "unknown") => string.Empty;

    /// <inheritdoc />
    public override async Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown")
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var ext = (extension ?? string.Empty).TrimStart('.');
        if (ext.Length > 0 && !ext.All(char.IsAsciiLetterOrDigit))
        {
            throw new StorageException("The file extension is not valid.", ProviderName, "Store", container, null,
                HttpStatusCode.BadRequest, code: "STORAGE_INVALID_PATH");
        }

        var name = ext.Length == 0 ? Guid.NewGuid().ToString() : $"{Guid.NewGuid()}.{ext}";
        return await StoreAsync(name, buffer, container).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown")
    {
        ArgumentNullException.ThrowIfNull(buffer);
        using var stream = new MemoryStream(buffer, writable: false);
        return await StoreAsync(stream, filePath, null, container).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<string> StoreAsync(Stream content, string filePath, StorageWriteOptions? options = null,
        string container = "unknown", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var (containerClient, blob, relative) = GetBlob("Store", container, filePath);
        try
        {
            var upload = new BlobUploadOptions
            {
                HttpHeaders = new BlobHttpHeaders
                {
                    ContentType = options?.ContentType ?? ContentTypeMap.FromExtension(Path.GetExtension(relative)),
                },
            };
            if (options is { Overwrite: false })
            {
                upload.Conditions = new BlobRequestConditions { IfNoneMatch = ETag.All };
            }

            var start = content.CanSeek ? content.Position : 0;
            try
            {
                await UploadOnceAsync(containerClient, blob, content, upload, ct).ConfigureAwait(false);
            }
            catch (RequestFailedException e) when (e.ErrorCode == "ContainerNotFound")
            {
                // The container was deleted after this process created it: always forget the cached creation, so the next write
                // recreates it even when this one cannot be replayed.
                _createdContainers.TryRemove(containerClient.Name, out _);
                if (!content.CanSeek)
                {
                    // A non-seekable body (the service's counting stream) is partly consumed and cannot be replayed.
                    throw;
                }

                content.Position = start;
                await UploadOnceAsync(containerClient, blob, content, upload, ct).ConfigureAwait(false);
            }

            return relative;
        }
        catch (RequestFailedException e) when (options is { Overwrite: false } && (e.Status == 412 || e is { Status: 409, ErrorCode: "BlobAlreadyExists" }))
        {
            throw new StorageException("Object already exists.", ProviderName, "Store", container, relative,
                HttpStatusCode.Conflict, e, "STORAGE_OBJECT_EXISTS");
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("Store", container, relative, e);
        }
    }

    /// <inheritdoc />
    public override Task<Stream?> RetrieveAsync(string filePath, string container = "unknown") =>
        RetrieveAsync(filePath, container, CancellationToken.None);

    /// <inheritdoc />
    public override async Task<Stream?> RetrieveAsync(string filePath, string container, CancellationToken ct)
    {
        var (_, blob, relative) = GetBlob("Retrieve", container, filePath);
        try
        {
            using var download = (await blob.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
            var memory = new MemoryStream();
            await download.Content.CopyToAsync(memory, ct).ConfigureAwait(false);
            memory.Position = 0;
            return memory;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("Retrieve", container, relative, e);
        }
    }

    /// <inheritdoc />
    public override async Task<Stream?> OpenReadAsync(string filePath, string container = "unknown",
        CancellationToken ct = default)
    {
        var (_, blob, relative) = GetBlob("OpenRead", container, filePath);
        try
        {
            var download = (await blob.DownloadStreamingAsync(cancellationToken: ct).ConfigureAwait(false)).Value;
            return download.Content;
        }
        catch (RequestFailedException e) when (e.Status == 404)
        {
            return null;
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("OpenRead", container, relative, e);
        }
    }

    /// <inheritdoc />
    public override async Task<bool> ExistsAsync(string filePath, string container = "unknown",
        CancellationToken ct = default)
    {
        var (_, blob, relative) = GetBlob("Exists", container, filePath);
        try
        {
            return (await blob.ExistsAsync(ct).ConfigureAwait(false)).Value;
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("Exists", container, relative, e);
        }
    }

    /// <inheritdoc />
    public override Task DeleteAsync(string filePath, string container = "unknown") =>
        DeleteAsync(filePath, container, CancellationToken.None);

    /// <inheritdoc />
    public override async Task DeleteAsync(string filePath, string container, CancellationToken ct)
    {
        var (_, blob, relative) = GetBlob("Delete", container, filePath);
        try
        {
            await blob.DeleteIfExistsAsync(DeleteSnapshotsOption.IncludeSnapshots, null, ct).ConfigureAwait(false);
            _logger.LogTrace("Deleted blob {BlobName} in container {Container}", blob.Name, container);
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("Delete", container, relative, e);
        }
    }

    /// <summary>Legacy: returns the absolute SAS URL as a string.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="mediaType">Ignored: the content type always comes from the object's extension.</param>
    /// <param name="fileName">Download file name; ignored when empty.</param>
    /// <param name="expiryDays">Ignored; the configured default SAS lifetime applies.</param>
    /// <param name="container">Container name.</param>
    /// <returns>The absolute SAS URL, or <c>null</c> when the client cannot sign.</returns>
    [Obsolete("Use GetPublicUrlAsync")]
    public override async Task<string?> GetPublicPath(string filePath, string mediaType, string fileName,
        int expiryDays = 20, string container = "unknown")
    {
        // The caller's mediaType is deliberately ignored: a client-chosen content type must not reach the SAS.
        var options = new PublicUrlOptions
        {
            DownloadFileName = string.IsNullOrWhiteSpace(fileName) ? null : fileName,
        };
        var uri = await GetPublicUrlAsync(filePath, options, container).ConfigureAwait(false);
        return uri?.ToString();
    }

    /// <inheritdoc />
    public override async Task<Uri?> GetPublicUrlAsync(string filePath, PublicUrlOptions options,
        string container = "unknown", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        var (containerClient, blob, relative) = GetBlob("GetPublicUrl", container, filePath);
        var expiresOn = DateTimeOffset.UtcNow.Add(options.GetEffectiveExpiry(_options.DefaultSasExpiry));
        // Only a safe-to-render type may be overridden; active content is always an attachment.
        var contentType = ContentTypeMap.ChooseResponseType(options.ContentType, relative);
        var inline = options.Inline && ContentTypeMap.IsSafeToRenderSigned(contentType);
        var builder = new BlobSasBuilder
        {
            BlobContainerName = containerClient.Name,
            BlobName = blob.Name,
            Resource = "b",
            ExpiresOn = expiresOn,
            ContentType = contentType,
            ContentDisposition = ContentDispositionFormatter.Format(options.DownloadFileName, inline),
        };
        builder.SetPermissions(BlobSasPermissions.Read);

        try
        {
            if (blob.CanGenerateSasUri)
            {
                return blob.GenerateSasUri(builder);
            }

            if (_options.Credential is null)
            {
                _logger.LogWarning("Cannot create a SAS URL for blob {BlobName}: configure a shared key or a token credential",
                    blob.Name);
                return null;
            }

            // Token credentials sign with a user-delegation key (no account key needed).
            // Azure rejects a delegation key valid for more than 7 days from its start, and a start in the future: start it a few
            // minutes back (clock skew), cap the window with a margin, and never let the SAS outlive the key.
            var keyStart = DateTimeOffset.UtcNow - DelegationKeyClockSkew;
            var keyExpiry = expiresOn <= keyStart.Add(MaxDelegationKeyLifetime) ? expiresOn : keyStart.Add(MaxDelegationKeyLifetime);
            builder.ExpiresOn = keyExpiry;
            var key = await _serviceClient.GetUserDelegationKeyAsync(keyStart, keyExpiry, ct).ConfigureAwait(false);
            return blob.GenerateUserDelegationSasUri(builder, key.Value);
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("GetPublicUrl", container, relative, e);
        }
    }

    private static readonly TimeSpan DelegationKeyClockSkew = TimeSpan.FromMinutes(5);

    // Azure's limit is 7 days from the key's start; one minute of margin.
    private static readonly TimeSpan MaxDelegationKeyLifetime = TimeSpan.FromDays(7) - TimeSpan.FromMinutes(1);

    private static bool IsUpstream(Exception e) =>
        e is RequestFailedException or HttpRequestException or IOException or TimeoutException or AggregateException;

    private (BlobContainerClient Container, BlobClient Blob, string Relative) GetBlob(string operation, string container,
        string filePath)
    {
        var relative = NormalizePath(ProviderName, operation, container, filePath);

        // Reject unsafe input first (traversal, nesting, control characters); only then map the logical name to Azure's rules.
        var safeContainer = NormalizeContainer(ProviderName, operation, container);
        string containerName;
        try
        {
            containerName = ContainerNameNormalizer.Normalize(safeContainer);
        }
        catch (ArgumentException)
        {
            throw new StorageException("The storage container is not valid.", ProviderName, operation, container, null,
                HttpStatusCode.BadRequest, code: "STORAGE_INVALID_CONTAINER");
        }

        var containerClient = _serviceClient.GetBlobContainerClient(containerName);
        var blobName = _options.LowercaseBlobNames ? relative.ToLowerInvariant() : relative;
        return (containerClient, containerClient.GetBlobClient(blobName), relative);
    }

    private async Task UploadOnceAsync(BlobContainerClient containerClient, BlobClient blob, Stream content,
        BlobUploadOptions upload, CancellationToken ct)
    {
        await EnsureContainerAsync(containerClient).ConfigureAwait(false);
        _logger.LogTrace("Uploading blob {BlobName} to container {Container}", blob.Name, containerClient.Name);
        await blob.UploadAsync(content, upload, ct).ConfigureAwait(false);
    }

    private Task EnsureContainerAsync(BlobContainerClient containerClient)
    {
        var task = _createdContainers.GetOrAdd(containerClient.Name,
            _ => containerClient.CreateIfNotExistsAsync(cancellationToken: CancellationToken.None));
        return AwaitAsync(task, containerClient.Name);
    }

    private async Task AwaitAsync(Task task, string name)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch
        {
            // Do not cache a failed creation: the next write retries.
            _createdContainers.TryRemove(name, out _);
            throw;
        }
    }

    private StorageException Failure(string operation, string container, string? relative, Exception inner)
    {
        _logger.LogError(inner, "Azure Blob {Operation} failed for container {Container}", operation, container);
        return new StorageException($"Azure Blob {operation} failed.", ProviderName, operation, container, relative,
            inner: inner);
    }
}
