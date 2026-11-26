using TabTabGo.Storage.Models;

namespace TabTabGo.Storage;

/// <summary>
/// Port to a blob/file backend. Contract shared by every provider:
/// <list type="bullet">
/// <item><description><c>RetrieveAsync</c> and <c>OpenReadAsync</c> return <c>null</c> only when the object does not exist.</description></item>
/// <item><description><c>StoreAsync</c> returns the stored relative path and never <c>null</c>; it overwrites by default.</description></item>
/// <item><description><c>DeleteAsync</c> is idempotent.</description></item>
/// <item><description>Every other failure throws <see cref="Exceptions.StorageException"/>.</description></item>
/// <item><description>Every path goes through <see cref="Paths.StoragePath.Normalize(string)"/>.</description></item>
/// </list>
/// The members added after 6.x are default interface methods, so third-party implementations keep compiling.
/// </summary>
public interface IStorageProvider
{
    /// <summary>Get default root directory for the storage provider.</summary>
    /// <param name="container">Container (logical folder) name.</param>
    /// <returns>The logical root of the container.</returns>
    string GetRootDirectory(string container = "unknown");

    /// <summary>Store a new file under a generated name.</summary>
    /// <param name="buffer">Bytes to store.</param>
    /// <param name="extension">File extension.</param>
    /// <param name="container">Container name.</param>
    /// <returns>The stored relative path.</returns>
    Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown");

    /// <summary>Store bytes at a given relative path, replacing any existing object.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="buffer">Bytes to store.</param>
    /// <param name="container">Container name.</param>
    /// <returns>The stored relative path.</returns>
    Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown");

    /// <summary>Read an object fully into a seekable stream positioned at 0.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="container">Container name.</param>
    /// <returns>The content, or <c>null</c> when the object does not exist.</returns>
    Task<Stream?> RetrieveAsync(string filePath, string container = "unknown");

    /// <summary>Delete an object. Deleting a missing object is a no-op.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="container">Container name.</param>
    /// <returns>A task that completes when the object is gone.</returns>
    Task DeleteAsync(string filePath, string container = "unknown");

    /// <summary>Legacy public location. Prefer <see cref="GetPublicUrlAsync"/>.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="mediaType">Content type override.</param>
    /// <param name="fileName">Download file name.</param>
    /// <param name="expiryDays">Requested lifetime in days.</param>
    /// <param name="container">Container name.</param>
    /// <returns>A location string, or <c>null</c>.</returns>
    [Obsolete("Use GetPublicUrlAsync")]
    Task<string?> GetPublicPath(string filePath, string mediaType, string fileName, int expiryDays = 20,
        string container = "unknown");

    /// <summary>Stream content to the backend without buffering the caller's copy where the backend allows it.</summary>
    /// <param name="content">Content to store.</param>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="options">Content type and overwrite behaviour.</param>
    /// <param name="container">Container name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The stored relative path.</returns>
    async Task<string> StoreAsync(Stream content, string filePath, StorageWriteOptions? options = null,
        string container = "unknown", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return await StoreAsync(filePath, buffer.ToArray(), container).ConfigureAwait(false);
    }

    /// <summary>Read an object fully into a seekable stream positioned at 0.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="container">Container name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The content, or <c>null</c> when the object does not exist.</returns>
    Task<Stream?> RetrieveAsync(string filePath, string container, CancellationToken ct) =>
        RetrieveAsync(filePath, container);

    /// <summary>Open an object for streaming. The stream may be non-seekable; the caller disposes it.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="container">Container name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The content, or <c>null</c> when the object does not exist.</returns>
    Task<Stream?> OpenReadAsync(string filePath, string container = "unknown", CancellationToken ct = default) =>
        RetrieveAsync(filePath, container, ct);

    /// <summary>Test whether an object exists.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="container">Container name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns><c>true</c> when the object exists.</returns>
    async Task<bool> ExistsAsync(string filePath, string container = "unknown", CancellationToken ct = default)
    {
        var stream = await RetrieveAsync(filePath, container, ct).ConfigureAwait(false);
        if (stream is null)
        {
            return false;
        }

        await stream.DisposeAsync().ConfigureAwait(false);
        return true;
    }

    /// <summary>Delete an object. Deleting a missing object is a no-op.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="container">Container name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>A task that completes when the object is gone.</returns>
    Task DeleteAsync(string filePath, string container, CancellationToken ct) => DeleteAsync(filePath, container);

    /// <summary>Create a time-limited URL a client can fetch the object from directly.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="options">Lifetime and response-header overrides.</param>
    /// <param name="container">Container name.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>An absolute URL, or <c>null</c> when the backend cannot provide one.</returns>
    async Task<Uri?> GetPublicUrlAsync(string filePath, PublicUrlOptions options, string container = "unknown",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
#pragma warning disable CS0618 // Default implementation delegates to the legacy member.
        var path = await GetPublicPath(filePath, options.ContentType ?? string.Empty, options.DownloadFileName ?? string.Empty,
            (int)Math.Max(1, Math.Ceiling(options.GetEffectiveExpiry(TimeSpan.FromDays(1)).TotalDays)), container)
            .ConfigureAwait(false);
#pragma warning restore CS0618
        return Uri.TryCreate(path, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }
}
