using System.Globalization;
using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using TabTabGo.Storage.ContentTypes;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.Paths;
using TabTabGo.Storage.Services;

namespace TabTabGo.Storage.FileStorage;

/// <summary>
/// Stores objects on the local file system. Every operation resolves through one resolver
/// (<see cref="IPathService.ResolvePath"/>) and refuses to leave the container root.
/// </summary>
public class FileStorageProvider : StorageProvider
{
    private const string ProviderName = "FileStorage";
    private const int NameAttempts = 10;

    private readonly IPathService _pathService;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<FileStorageProvider> _logger;

    /// <summary>Creates a provider using the system clock and no logging.</summary>
    /// <param name="pathService">Path configuration and resolver.</param>
    public FileStorageProvider(IPathService pathService)
        : this(pathService, null, null)
    {
    }

    /// <summary>Creates a provider.</summary>
    /// <param name="pathService">Path configuration and resolver.</param>
    /// <param name="timeProvider">Clock for the date folder (UTC). Defaults to the system clock.</param>
    /// <param name="logger">Logger.</param>
    public FileStorageProvider(IPathService pathService, TimeProvider? timeProvider,
        ILogger<FileStorageProvider>? logger = null)
    {
        _pathService = pathService ?? throw new ArgumentNullException(nameof(pathService));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _logger = logger ?? NullLogger<FileStorageProvider>.Instance;
    }

    /// <summary>
    /// Logical root of a container: <c>RootPath</c> plus the mapped folder (or the container name when unmapped).
    /// Not resolved against the content root.
    /// </summary>
    /// <param name="container">Container name.</param>
    /// <returns>The logical root.</returns>
    public override string GetRootDirectory(string container = "unknown")
    {
        var name = NormalizeContainer(ProviderName, "GetRootDirectory", container);
        var mapped = _pathService.PathSettings.TryGetPath(name, out var folder) ? folder : name;
        // Configuration written on Windows uses backslashes; treat them as separators on every OS.
        return Path.Combine((_pathService.PathSettings.RootPath ?? string.Empty).Replace('\\', '/'), mapped.Replace('\\', '/'));
    }

    /// <inheritdoc />
    public override async Task<string> StoreAsync(byte[] buffer, string extension = ".tmp",
        string container = "unknown")
    {
        ArgumentNullException.ThrowIfNull(buffer);
        using var stream = new MemoryStream(buffer, writable: false);
        return await StoreGeneratedAsync(stream, extension, container, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown")
    {
        ArgumentNullException.ThrowIfNull(buffer);
        using var stream = new MemoryStream(buffer, writable: false);
        return await WriteAsync(stream, filePath, true, container, CancellationToken.None).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override Task<string> StoreAsync(Stream content, string filePath, StorageWriteOptions? options = null,
        string container = "unknown", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        return WriteAsync(content, filePath, options?.Overwrite ?? true, container, ct);
    }

    /// <inheritdoc />
    public override Task<Stream?> RetrieveAsync(string filePath, string container = "unknown") =>
        RetrieveAsync(filePath, container, CancellationToken.None);

    /// <inheritdoc />
    public override async Task<Stream?> RetrieveAsync(string filePath, string container, CancellationToken ct)
    {
        var source = await OpenReadAsync(filePath, container, ct).ConfigureAwait(false);
        if (source is null)
        {
            return null;
        }

        await using (source.ConfigureAwait(false))
        {
            var memory = new MemoryStream();
            try
            {
                await source.CopyToAsync(memory, ct).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                await memory.DisposeAsync().ConfigureAwait(false);
                throw Failure("Retrieve", container, filePath, e);
            }

            memory.Position = 0;
            return memory;
        }
    }

    /// <inheritdoc />
    public override Task<Stream?> OpenReadAsync(string filePath, string container = "unknown",
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var full = Resolve("OpenRead", container, filePath).FullPath;
        try
        {
            if (!File.Exists(full))
            {
                return Task.FromResult<Stream?>(null);
            }

            Stream stream = new FileStream(full, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return Task.FromResult<Stream?>(stream);
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return Task.FromResult<Stream?>(null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Failure("OpenRead", container, filePath, e);
        }
    }

    /// <inheritdoc />
    public override Task<bool> ExistsAsync(string filePath, string container = "unknown",
        CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(File.Exists(Resolve("Exists", container, filePath).FullPath));
    }

    /// <inheritdoc />
    public override Task DeleteAsync(string filePath, string container = "unknown") =>
        DeleteAsync(filePath, container, CancellationToken.None);

    /// <inheritdoc />
    public override Task DeleteAsync(string filePath, string container, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var full = Resolve("Delete", container, filePath).FullPath;
        try
        {
            if (File.Exists(full))
            {
                File.Delete(full);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Failure("Delete", container, filePath, e);
        }

        return Task.CompletedTask;
    }

    /// <summary>Legacy: returns the relative path unchanged (a file system has no URL).</summary>
    /// <param name="filePath">Relative object path.</param>
    /// <param name="mediaType">Ignored.</param>
    /// <param name="fileName">Ignored.</param>
    /// <param name="expiryDays">Ignored.</param>
    /// <param name="container">Ignored.</param>
    /// <returns>The path as supplied.</returns>
    [Obsolete("Use GetPublicUrlAsync")]
    public override Task<string?> GetPublicPath(string filePath, string mediaType, string fileName,
        int expiryDays = 20, string container = "unknown") => Task.FromResult<string?>(filePath);

    /// <inheritdoc />
    public override Task<Uri?> GetPublicUrlAsync(string filePath, PublicUrlOptions options,
        string container = "unknown", CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        ct.ThrowIfCancellationRequested();
        var relative = NormalizePath(ProviderName, "GetPublicUrl", container, filePath, StoragePathLimits.FileSystem);
        var baseUrl = _pathService.PathSettings.PublicBaseUrl;
        if (string.IsNullOrWhiteSpace(baseUrl))
        {
            return Task.FromResult<Uri?>(null);
        }

        var name = NormalizeContainer(ProviderName, "GetPublicUrl", container);
        var folder = _pathService.PathSettings.TryGetPath(name, out var mapped) && !Path.IsPathRooted(mapped)
            ? mapped.Replace('\\', '/')
            : name;
        var segments = (folder + "/" + relative).Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Select(Uri.EscapeDataString);
        var url = baseUrl.TrimEnd('/') + "/" + string.Join('/', segments);
        return Task.FromResult(Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
                               (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null);
    }

    private async Task<string> StoreGeneratedAsync(Stream content, string extension, string container,
        CancellationToken ct)
    {
        var ext = (extension ?? string.Empty).TrimStart('.');
        if (ext.Length > 0 && !ext.All(char.IsAsciiLetterOrDigit))
        {
            throw new StorageException("The file extension is not valid.", ProviderName, "Store", container, null,
                HttpStatusCode.BadRequest, code: "STORAGE_INVALID_PATH");
        }

        var folder = _timeProvider.GetUtcNow()
            .ToString(_pathService.PathSettings.DateFormat ?? "yyyyMMdd", CultureInfo.InvariantCulture);
        for (var attempt = 0; attempt < NameAttempts; attempt++)
        {
            var random = Path.GetFileNameWithoutExtension(Path.GetRandomFileName());
            var name = ext.Length == 0 ? random : $"{random}.{ext}";
            content.Position = 0;
            try
            {
                return await WriteAsync(content, $"{folder}/{name}", false, container, ct).ConfigureAwait(false);
            }
            catch (StorageException e) when (e.ErrorCode == "STORAGE_OBJECT_EXISTS")
            {
                // Name collision: try another random name.
            }
        }

        throw new StorageException("Could not allocate a unique file name.", ProviderName, "Store", container, null,
            code: "STORAGE_NAME_EXHAUSTED");
    }

    private async Task<string> WriteAsync(Stream content, string filePath, bool overwrite, string container,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var (full, relative) = Resolve("Store", container, filePath);
        var directory = Path.GetDirectoryName(full)!;
        if (!overwrite)
        {
            return await WriteExclusiveAsync(content, full, relative, container, ct).ConfigureAwait(false);
        }

        var temp = Path.Combine(directory, "." + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            Directory.CreateDirectory(directory);
            var target = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920,
                FileOptions.Asynchronous);
            await using (target.ConfigureAwait(false))
            {
                await content.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            // Write to a temp file in the same folder, then move: readers never see a half-written object.
            File.Move(temp, full, overwrite);
            return relative;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Failure("Store", container, relative, e);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    // No-overwrite stores claim the name with an exclusive create, which exactly one concurrent writer can win on every
    // OS (File.Move without overwrite is not atomic on every platform). A failed write removes the partial file.
    private async Task<string> WriteExclusiveAsync(Stream content, string full, string relative, string container,
        CancellationToken ct)
    {
        FileStream target;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            target = new FileStream(full, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
        }
        catch (IOException) when (File.Exists(full))
        {
            throw new StorageException("Object already exists.", ProviderName, "Store", container, relative,
                HttpStatusCode.Conflict, code: "STORAGE_OBJECT_EXISTS");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            throw Failure("Store", container, relative, e);
        }

        try
        {
            await using (target.ConfigureAwait(false))
            {
                await content.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            return relative;
        }
        catch (Exception e)
        {
            TryDelete(full);
            if (e is IOException or UnauthorizedAccessException)
            {
                throw Failure("Store", container, relative, e);
            }

            throw;
        }
    }

    private (string FullPath, string Relative) Resolve(string operation, string container, string filePath)
    {
        var relative = NormalizePath(ProviderName, operation, container, filePath, StoragePathLimits.FileSystem);
        var root = Path.GetFullPath(_pathService.ResolvePath(GetRootDirectory(container)));
        var full = Path.GetFullPath(Path.Combine(root, relative));
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        var prefix = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                     Path.DirectorySeparatorChar;
        if (!full.StartsWith(prefix, comparison))
        {
            // Defence in depth: StoragePath already rejects traversal.
            throw new StorageException("The storage path escapes the container root.", ProviderName, operation,
                container, null, HttpStatusCode.BadRequest, code: "STORAGE_INVALID_PATH");
        }

        // A symbolic link planted inside the container must not lead out of it: compare the real locations.
        var realRoot = RealPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                       Path.DirectorySeparatorChar;
        if (!RealPath(full).StartsWith(realRoot, comparison))
        {
            throw new StorageException("The storage path escapes the container root.", ProviderName, operation,
                container, null, HttpStatusCode.BadRequest, code: "STORAGE_INVALID_PATH");
        }

        return (full, relative);
    }

    // Resolves every existing component that is a symbolic link (left to right), like realpath().
    private static string RealPath(string fullPath)
    {
        var root = Path.GetPathRoot(fullPath) ?? string.Empty;
        var current = root;
        foreach (var segment in fullPath[root.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            try
            {
                FileSystemInfo info = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
                var target = info.ResolveLinkTarget(returnFinalTarget: true);
                if (target is not null)
                {
                    current = target.FullName;
                }
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Not a readable link: treat as an ordinary path component.
            }
        }

        return current;
    }

    private StorageException Failure(string operation, string container, string? filePath, Exception inner)
    {
        _logger.LogError(inner, "File storage {Operation} failed for container {Container}", operation, container);
        return new StorageException($"File storage {operation} failed.", ProviderName, operation, container, filePath,
            inner: inner);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Best effort: a leftover dot-prefixed temp file is harmless.
        }
    }
}
