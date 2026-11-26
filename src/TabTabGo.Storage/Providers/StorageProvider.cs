using System.Net;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.Paths;

namespace TabTabGo.Storage;

/// <summary>
/// Base class for providers. The members added after 6.x are virtual and delegate to the legacy abstract members, so
/// existing subclasses keep working; the built-in providers override them natively.
/// </summary>
public abstract class StorageProvider : IStorageProvider
{
    /// <inheritdoc />
    public abstract string GetRootDirectory(string container = "unknown");

    /// <inheritdoc />
    public abstract Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown");

    /// <inheritdoc />
    public abstract Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown");

    /// <inheritdoc />
    public abstract Task<Stream?> RetrieveAsync(string filePath, string container = "unknown");

    /// <inheritdoc />
    public abstract Task DeleteAsync(string filePath, string container = "unknown");

    /// <inheritdoc />
    [Obsolete("Use GetPublicUrlAsync")]
    public abstract Task<string?> GetPublicPath(string filePath, string mediaType, string fileName, int expiryDays = 20,
        string container = "unknown");

    /// <inheritdoc />
    public virtual async Task<string> StoreAsync(Stream content, string filePath, StorageWriteOptions? options = null,
        string container = "unknown", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return await StoreAsync(filePath, buffer.ToArray(), container).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual Task<Stream?> RetrieveAsync(string filePath, string container, CancellationToken ct) =>
        RetrieveAsync(filePath, container);

    /// <inheritdoc />
    public virtual Task<Stream?> OpenReadAsync(string filePath, string container = "unknown",
        CancellationToken ct = default) => RetrieveAsync(filePath, container, ct);

    /// <inheritdoc />
    public virtual async Task<bool> ExistsAsync(string filePath, string container = "unknown",
        CancellationToken ct = default)
    {
        var stream = await RetrieveAsync(filePath, container, ct).ConfigureAwait(false);
        if (stream is null)
        {
            return false;
        }

        await stream.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <inheritdoc />
    public virtual Task DeleteAsync(string filePath, string container, CancellationToken ct) =>
        DeleteAsync(filePath, container);

    /// <inheritdoc />
    public virtual async Task<Uri?> GetPublicUrlAsync(string filePath, PublicUrlOptions options,
        string container = "unknown", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
#pragma warning disable CS0618 // Default implementation delegates to the legacy member.
        var path = await GetPublicPath(filePath, options.ContentType ?? string.Empty,
            options.DownloadFileName ?? string.Empty,
            (int)Math.Max(1, Math.Ceiling(options.GetEffectiveExpiry(TimeSpan.FromDays(1)).TotalDays)), container)
            .ConfigureAwait(false);
#pragma warning restore CS0618
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) &&
               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    /// <summary>
    /// Normalises an object path, converting an unsafe path into a 400 <see cref="StorageException"/>.
    /// </summary>
    /// <param name="provider">Provider name for the exception.</param>
    /// <param name="operation">Operation name for the exception.</param>
    /// <param name="container">Container for the exception.</param>
    /// <param name="filePath">Path to normalise.</param>
    /// <returns>The normalised relative path.</returns>
    protected static string NormalizePath(string provider, string operation, string? container, string? filePath)
    {
        if (filePath is null || !StoragePath.IsSafe(filePath))
        {
            throw new StorageException("The storage path is not valid.", provider, operation, container, null,
                HttpStatusCode.BadRequest, code: "STORAGE_INVALID_PATH");
        }

        return StoragePath.Normalize(filePath);
    }

    /// <summary>
    /// Normalises an object path like <see cref="NormalizePath(string, string, string?, string?)"/> and also enforces
    /// the provider's <paramref name="limits"/>; an over-long path is a 400 <see cref="StorageException"/>
    /// (<c>STORAGE_INVALID_PATH</c>), not an upstream failure.
    /// </summary>
    /// <param name="provider">Provider name for the exception.</param>
    /// <param name="operation">Operation name for the exception.</param>
    /// <param name="container">Container for the exception.</param>
    /// <param name="filePath">Path to normalise.</param>
    /// <param name="limits">Length limits of the provider.</param>
    /// <returns>The normalised relative path.</returns>
    protected static string NormalizePath(string provider, string operation, string? container, string? filePath,
        StoragePathLimits limits)
    {
        ArgumentNullException.ThrowIfNull(limits);
        if (filePath is null || !StoragePath.IsSafe(filePath, limits))
        {
            throw new StorageException("The storage path is not valid.", provider, operation, container, null,
                HttpStatusCode.BadRequest, code: "STORAGE_INVALID_PATH");
        }

        return StoragePath.Normalize(filePath, limits);
    }

    /// <summary>
    /// Validates a container name (a single, safe path segment), converting anything else into a 400
    /// <see cref="StorageException"/>.
    /// </summary>
    /// <param name="provider">Provider name for the exception.</param>
    /// <param name="operation">Operation name for the exception.</param>
    /// <param name="container">Container name to normalise.</param>
    /// <returns>The normalised container name.</returns>
    protected static string NormalizeContainer(string provider, string operation, string? container)
    {
        // A container is exactly one path segment: nested names would alias other containers (a/b + c vs a + b/c).
        if (container is null || !StoragePath.IsSafe(container) || StoragePath.Normalize(container).Contains('/'))
        {
            throw new StorageException("The storage container is not valid.", provider, operation, container, null,
                HttpStatusCode.BadRequest, code: "STORAGE_INVALID_CONTAINER");
        }

        return StoragePath.Normalize(container);
    }
}
