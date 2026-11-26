using System.Collections.Concurrent;
using TabTabGo.Storage.ContentTypes;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;

namespace TabTabGo.Storage.Testing;

/// <summary>
/// Reference implementation of the <see cref="IStorageProvider"/> contract that keeps everything in memory.
/// Use it in consumers' tests instead of mocking the port. Thread-safe.
/// </summary>
public class InMemoryStorageProvider : StorageProvider
{
    private const string ProviderName = "InMemory";

    private readonly ConcurrentDictionary<string, StoredObject> _objects = new(StringComparer.Ordinal);

    /// <summary>Number of stored objects.</summary>
    public int Count => _objects.Count;

    /// <summary>Returns the stored content type of an object, or <c>null</c> when absent.</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="container">Container name.</param>
    /// <returns>The content type recorded at store time.</returns>
    public string? GetContentType(string filePath, string container = "unknown") =>
        _objects.TryGetValue(Key(container, filePath, "GetContentType"), out var stored) ? stored.ContentType : null;

    /// <summary>Lists the keys (<c>container/path</c>) of every stored object.</summary>
    /// <returns>A snapshot of the keys.</returns>
    public IReadOnlyCollection<string> Keys() => _objects.Keys.ToArray();

    /// <inheritdoc />
    public override string GetRootDirectory(string container = "unknown") => container + "/";

    /// <inheritdoc />
    public override Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown")
    {
        ArgumentNullException.ThrowIfNull(buffer);
        var ext = (extension ?? string.Empty).TrimStart('.');
        var name = ext.Length == 0 ? Guid.NewGuid().ToString("N") : $"{Guid.NewGuid():N}.{ext}";
        return StoreAsync(name, buffer, container);
    }

    /// <inheritdoc />
    public override Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown")
    {
        ArgumentNullException.ThrowIfNull(buffer);
        return Task.FromResult(Put(filePath, buffer.ToArray(), null, true, container));
    }

    /// <inheritdoc />
    public override async Task<string> StoreAsync(Stream content, string filePath, StorageWriteOptions? options = null,
        string container = "unknown", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer, ct).ConfigureAwait(false);
        return Put(filePath, buffer.ToArray(), options?.ContentType, options?.Overwrite ?? true, container);
    }

    /// <inheritdoc />
    public override Task<Stream?> RetrieveAsync(string filePath, string container = "unknown") =>
        RetrieveAsync(filePath, container, CancellationToken.None);

    /// <inheritdoc />
    public override Task<Stream?> RetrieveAsync(string filePath, string container, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        Stream? result = _objects.TryGetValue(Key(container, filePath, "Retrieve"), out var stored)
            ? new MemoryStream(stored.Data, writable: false)
            : null;
        return Task.FromResult(result);
    }

    /// <inheritdoc />
    public override Task<bool> ExistsAsync(string filePath, string container = "unknown", CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(_objects.ContainsKey(Key(container, filePath, "Exists")));
    }

    /// <inheritdoc />
    public override Task DeleteAsync(string filePath, string container = "unknown") =>
        DeleteAsync(filePath, container, CancellationToken.None);

    /// <inheritdoc />
    public override Task DeleteAsync(string filePath, string container, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        _objects.TryRemove(Key(container, filePath, "Delete"), out _);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    [Obsolete("Use GetPublicUrlAsync")]
    public override Task<string?> GetPublicPath(string filePath, string mediaType, string fileName, int expiryDays = 20,
        string container = "unknown") => Task.FromResult<string?>(BuildUrl(filePath, container));

    /// <inheritdoc />
    public override Task<Uri?> GetPublicUrlAsync(string filePath, PublicUrlOptions options, string container = "unknown",
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        return Task.FromResult<Uri?>(new Uri(BuildUrl(filePath, container)));
    }

    private static string BuildUrl(string filePath, string container) =>
        $"https://in-memory.invalid/{Uri.EscapeDataString(container)}/{NormalizePath(ProviderName, "GetPublicUrl", container, filePath)}";

    private string Put(string filePath, byte[] data, string? contentType, bool overwrite, string container)
    {
        var normalized = NormalizePath(ProviderName, "Store", container, filePath);
        var key = Key(container, normalized, "Store");
        var stored = new StoredObject(data, contentType ?? ContentTypeMap.FromExtension(Path.GetExtension(normalized)));
        if (overwrite)
        {
            _objects[key] = stored;
        }
        else if (!_objects.TryAdd(key, stored))
        {
            throw new StorageException("Object already exists.", ProviderName, "Store", container, normalized,
                System.Net.HttpStatusCode.Conflict, code: "STORAGE_OBJECT_EXISTS");
        }

        return normalized;
    }

    private static string Key(string container, string filePath, string operation) =>
        $"{NormalizeContainer(ProviderName, operation, container)}/{NormalizePath(ProviderName, operation, container, filePath)}";

    private sealed record StoredObject(byte[] Data, string ContentType);
}
