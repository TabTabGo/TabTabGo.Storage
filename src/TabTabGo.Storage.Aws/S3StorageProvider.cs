using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TabTabGo.Storage.ContentTypes;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.Paths;

namespace TabTabGo.Storage.Aws;

/// <summary>
/// AWS S3 implementation of <see cref="IStorageProvider"/>. A container is a key prefix inside one bucket.
/// Register it as a singleton with <c>AddTabTabGoS3Storage</c>; the injected <see cref="IAmazonS3"/> is shared.
/// </summary>
public class S3StorageProvider : StorageProvider, IDisposable
{
    private const string ProviderName = "S3";

    private readonly IAmazonS3? _client;
    private readonly ILogger<S3StorageProvider>? _logger;
    private readonly bool _ownsClient;
    private S3Configuration? _configuration;

    /// <summary>Unsupported: the provider needs a client and configuration. Use dependency injection.</summary>
    [Obsolete("Use AddTabTabGoS3Storage() or the (IAmazonS3, IOptions<S3Configuration>, ILogger) constructor.")]
    public S3StorageProvider()
    {
        throw new InvalidOperationException(
            "S3StorageProvider requires an IAmazonS3 client and S3Configuration. Register it with AddTabTabGoS3Storage().");
    }

    /// <summary>Creates the provider around a shared client.</summary>
    /// <param name="client">Shared S3 client (not disposed by the provider).</param>
    /// <param name="configuration">S3 configuration.</param>
    /// <param name="logger">Logger. Secrets are never logged.</param>
    public S3StorageProvider(IAmazonS3 client, IOptions<S3Configuration> configuration, ILogger<S3StorageProvider> logger)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _configuration = configuration?.Value ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>Legacy constructor: builds and owns its own client (disposed with the provider).</summary>
    /// <param name="configuration">S3 configuration.</param>
    /// <param name="logger">Logger.</param>
    [Obsolete("Use the (IAmazonS3, IOptions<S3Configuration>, ILogger) constructor so the client is shared.")]
    public S3StorageProvider(IOptions<S3Configuration> configuration, ILogger<S3StorageProvider> logger)
    {
        _configuration = configuration?.Value ?? throw new ArgumentNullException(nameof(configuration));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _client = S3ClientFactory.Create(_configuration);
        _ownsClient = true;
    }

    private S3Configuration Configuration =>
        _configuration ?? throw new InvalidOperationException("The provider is not configured.");

    private IAmazonS3 Client => _client ?? throw new InvalidOperationException("The provider is not configured.");

    /// <summary>Changes the bucket. Region and credentials cannot change on a live client.</summary>
    /// <param name="configuration">New configuration.</param>
    /// <exception cref="InvalidOperationException">The region, endpoint or keys differ from the current ones.</exception>
    [Obsolete("Register a new provider instead; region and credentials cannot change on a live client.")]
    public void SetConfiguration(S3Configuration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var current = Configuration;
        if (!string.Equals(current.Region, configuration.Region, StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(current.ServiceUrl, configuration.ServiceUrl, StringComparison.Ordinal) ||
            !string.Equals(current.AccessKey, configuration.AccessKey, StringComparison.Ordinal) ||
            !string.Equals(current.SecretKey, configuration.SecretKey, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Region, endpoint and credentials cannot change on a live S3 client. Create a new provider.");
        }

        _configuration = configuration;
    }

    /// <inheritdoc />
    public override string GetRootDirectory(string container = "unknown") => $"{container}/";

    /// <inheritdoc />
    public override Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown")
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var ext = (extension ?? string.Empty).TrimStart('.');
        if (ext.Length > 0 && !ext.All(char.IsAsciiLetterOrDigit))
        {
            throw new StorageException("The file extension is not valid.", ProviderName, "Store", container, null,
                HttpStatusCode.BadRequest, code: "STORAGE_INVALID_PATH");
        }

        var name = ext.Length == 0 ? Guid.NewGuid().ToString() : $"{Guid.NewGuid()}.{ext}";
        return StoreAsync(name, buffer, container);
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
        var relative = NormalizePath(ProviderName, "Store", container, filePath, StoragePathLimits.S3ObjectKey(container));
        var key = Key("Store", container, relative);
        MemoryStream? buffered = null;
        try
        {
            var input = content;
            if (!content.CanSeek)
            {
                // PutObject needs a known length; non-seekable sources are buffered.
                buffered = new MemoryStream();
                await content.CopyToAsync(buffered, ct).ConfigureAwait(false);
                buffered.Position = 0;
                input = buffered;
            }

            var request = new PutObjectRequest
            {
                BucketName = Configuration.BucketName,
                Key = key,
                InputStream = input,
                AutoCloseStream = false,
                ContentType = options?.ContentType ?? ContentTypeMap.FromExtension(Path.GetExtension(relative)),
                // One conditional PUT instead of HEAD-then-PUT: no race between concurrent writers.
                IfNoneMatch = options is { Overwrite: false } ? "*" : null,
            };

            await Client.PutObjectAsync(request, ct).ConfigureAwait(false);
            return relative;
        }
        catch (AmazonS3Exception e) when (options is { Overwrite: false } &&
                                          (e.StatusCode == HttpStatusCode.PreconditionFailed ||
                                           e.StatusCode == HttpStatusCode.Conflict))
        {
            throw new StorageException("Object already exists.", ProviderName, "Store", container, relative,
                HttpStatusCode.Conflict, e, "STORAGE_OBJECT_EXISTS");
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("Store", container, relative, e);
        }
        finally
        {
            if (buffered is not null)
            {
                await buffered.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    /// <inheritdoc />
    public override Task<Stream?> RetrieveAsync(string filePath, string container = "unknown") =>
        RetrieveAsync(filePath, container, CancellationToken.None);

    /// <inheritdoc />
    public override async Task<Stream?> RetrieveAsync(string filePath, string container, CancellationToken ct)
    {
        var relative = NormalizePath(ProviderName, "Retrieve", container, filePath, StoragePathLimits.S3ObjectKey(container));
        try
        {
            using var response = await Client.GetObjectAsync(RequestFor("Retrieve", container, relative), ct)
                .ConfigureAwait(false);
            var memory = new MemoryStream();
            await response.ResponseStream.CopyToAsync(memory, ct).ConfigureAwait(false);
            memory.Position = 0;
            return memory;
        }
        catch (AmazonS3Exception e) when (IsNotFound(e) || IsForbidden(e))
        {
            LogForbiddenAsNotFound(e, "Retrieve", container, relative);
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
        var relative = NormalizePath(ProviderName, "OpenRead", container, filePath, StoragePathLimits.S3ObjectKey(container));
        try
        {
            var response = await Client.GetObjectAsync(RequestFor("OpenRead", container, relative), ct)
                .ConfigureAwait(false);
            return new S3ResponseStream(response);
        }
        catch (AmazonS3Exception e) when (IsNotFound(e) || IsForbidden(e))
        {
            LogForbiddenAsNotFound(e, "OpenRead", container, relative);
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
        var relative = NormalizePath(ProviderName, "Exists", container, filePath, StoragePathLimits.S3ObjectKey(container));
        return await ExistsCoreAsync(Key("Exists", container, relative), container, relative, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task DeleteAsync(string filePath, string container = "unknown") =>
        DeleteAsync(filePath, container, CancellationToken.None);

    /// <inheritdoc />
    public override async Task DeleteAsync(string filePath, string container, CancellationToken ct)
    {
        var relative = NormalizePath(ProviderName, "Delete", container, filePath, StoragePathLimits.S3ObjectKey(container));
        try
        {
            // S3 reports success for a missing key, so deletion is naturally idempotent.
            await Client.DeleteObjectAsync(
                new DeleteObjectRequest { BucketName = Configuration.BucketName, Key = Key("Delete", container, relative) },
                ct).ConfigureAwait(false);
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("Delete", container, relative, e);
        }
    }

    /// <summary>Legacy: returns a presigned URL using the configured default lifetime.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="mediaType">Ignored: the content type always comes from the object's extension.</param>
    /// <param name="fileName">Download file name; ignored when empty.</param>
    /// <param name="expiryDays">Ignored; the configured <c>ExpirationMinutes</c> applies.</param>
    /// <param name="container">Container name.</param>
    /// <returns>The presigned URL.</returns>
    [Obsolete("Use GetPublicUrlAsync")]
    public override async Task<string?> GetPublicPath(string filePath, string mediaType, string fileName,
        int expiryDays = 20, string container = "unknown")
    {
        // The caller's mediaType is deliberately ignored: a client-chosen content type must not reach the signed URL.
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
        var relative = NormalizePath(ProviderName, "GetPublicUrl", container, filePath, StoragePathLimits.S3ObjectKey(container));
        var expiry = options.GetEffectiveExpiry(TimeSpan.FromMinutes(Configuration.ExpirationMinutes));
        var request = new GetPreSignedUrlRequest
        {
            BucketName = Configuration.BucketName,
            Key = Key("GetPublicUrl", container, relative),
            Verb = HttpVerb.GET,
            Expires = DateTime.UtcNow.Add(expiry),
            Protocol = Configuration.ServiceUrl?.StartsWith("http://", StringComparison.OrdinalIgnoreCase) == true
                ? Protocol.HTTP
                : Protocol.HTTPS,
        };

        // Only a safe-to-render type may be overridden; anything else falls back to the extension's type, and
        // active content is always an attachment.
        var contentType = ContentTypeMap.ChooseResponseType(options.ContentType, relative);
        var inline = options.Inline && ContentTypeMap.IsSafeToRenderSigned(contentType);
        request.ResponseHeaderOverrides = new ResponseHeaderOverrides
        {
            ContentType = contentType,
            ContentDisposition = ContentDispositionFormatter.Format(options.DownloadFileName, inline),
        };

        try
        {
            var url = await Client.GetPreSignedURLAsync(request).ConfigureAwait(false);
            return new Uri(url);
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("GetPublicUrl", container, relative, e);
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_ownsClient)
        {
            _client?.Dispose();
        }

        GC.SuppressFinalize(this);
    }

    private static bool IsNotFound(AmazonS3Exception e) =>
        e.StatusCode == HttpStatusCode.NotFound || e.ErrorCode is "NoSuchKey" or "NotFound";

    // S3 answers 403 for a missing key when the caller lacks s3:ListBucket, so a read 403 is reported as not found.
    private static bool IsForbidden(AmazonS3Exception e) => e.StatusCode == HttpStatusCode.Forbidden;

    private void LogForbiddenAsNotFound(AmazonS3Exception e, string operation, string container, string relative)
    {
        if (!IsForbidden(e))
        {
            return;
        }

        // Provider, operation, container and the normalised relative path only: never keys, credentials or URLs.
        _logger?.LogWarning(
            "S3 {Operation} was denied (403) and is reported as not found: provider {Provider}, container {Container}, path {Path}. " +
            "Grant s3:ListBucket to tell a missing key from a permission fault",
            operation, ProviderName, container, relative);
    }

    private static bool IsUpstream(Exception e) =>
        e is AmazonServiceException or AmazonClientException or HttpRequestException or IOException
            or TimeoutException;

    private string Key(string operation, string container, string relative) =>
        $"{NormalizeContainer(ProviderName, operation, container)}/{relative}";

    private GetObjectRequest RequestFor(string operation, string container, string relative) => new()
    {
        BucketName = Configuration.BucketName,
        Key = Key(operation, container, relative),
    };

    private async Task<bool> ExistsCoreAsync(string key, string container, string relative, CancellationToken ct)
    {
        try
        {
            await Client.GetObjectMetadataAsync(
                new GetObjectMetadataRequest { BucketName = Configuration.BucketName, Key = key }, ct)
                .ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception e) when (IsNotFound(e) || IsForbidden(e))
        {
            LogForbiddenAsNotFound(e, "Exists", container, relative);
            return false;
        }
        catch (Exception e) when (IsUpstream(e))
        {
            throw Failure("Exists", container, relative, e);
        }
    }

    private StorageException Failure(string operation, string container, string? relative, Exception inner)
    {
        // Message and log carry the operation and container only; never keys, credentials or signed URLs.
        _logger?.LogError(inner, "S3 {Operation} failed for container {Container}", operation, container);
        return new StorageException($"S3 {operation} failed.", ProviderName, operation, container, relative,
            inner: inner);
    }
}
