using System.Linq.Expressions;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TabTabGo.Core.Data;
using TabTabGo.Core.Enums;
using TabTabGo.Core.Exceptions;
using TabTabGo.Core.Models;
using TabTabGo.Core.Services;
using TabTabGo.Storage.ContentTypes;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.Paths;
using TabTabGo.Storage.Services.Internal;
using TabTabGo.Storage.ViewModels;

namespace TabTabGo.Storage.Services;

// Core 8.0.1 declares BaseService/BaseReadService members whose nullability differs from IBaseService/IBaseReadService.
// Any subclass inherits those mismatches; they are Core's to fix (reported), not Storage's.
#pragma warning disable CS8767, CS8614, CS8765

/// <summary>
/// Upload, replace, update, read and delete for file metadata and content. Storage-owned fields (path, size,
/// extension, original name and media type) change only through <see cref="Upload"/> and <see cref="Replace"/>;
/// client updates are limited to an allow-list.
/// </summary>
/// <typeparam name="TFile">File entity type.</typeparam>
public class FileService<TFile> : BaseService<TFile, long>, IFileService<TFile> where TFile : class, IFile
{
    private static readonly ImageSize[] ThumbnailSizes =
        Enum.GetValues<ImageSize>().Where(s => s != ImageSize.Default).ToArray();

    private readonly IImageService _imageService;
    private readonly IStorageProvider _storageProvider;
    private readonly StorageServiceOptions _options;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates the service with default upload rules and the system clock.</summary>
    /// <param name="unitOfWork">Unit of work.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="storageProvider">Storage provider.</param>
    /// <param name="imageService">Image service.</param>
    public FileService(IUnitOfWork unitOfWork, ILogger<FileService<TFile>> logger, IStorageProvider storageProvider,
        IImageService imageService)
        : this(unitOfWork, logger, storageProvider, imageService, null, null)
    {
    }

    /// <summary>Creates the service.</summary>
    /// <param name="unitOfWork">Unit of work.</param>
    /// <param name="logger">Logger.</param>
    /// <param name="storageProvider">Storage provider.</param>
    /// <param name="imageService">Image service.</param>
    /// <param name="options">Upload rules; defaults when <c>null</c>.</param>
    /// <param name="timeProvider">Clock for date folders (UTC); the system clock when <c>null</c>.</param>
    public FileService(IUnitOfWork unitOfWork, ILogger<FileService<TFile>> logger, IStorageProvider storageProvider,
        IImageService imageService, IOptions<StorageServiceOptions>? options, TimeProvider? timeProvider)
        : base(unitOfWork, logger)
    {
        _storageProvider = storageProvider ?? throw new ArgumentNullException(nameof(storageProvider));
        _imageService = imageService ?? throw new ArgumentNullException(nameof(imageService));
        _options = options?.Value ?? new StorageServiceOptions();
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    private IGenericRepository<TFile, long> Repo => _unitOfWork.Repository<TFile, long>();

    /// <inheritdoc />
    public override Expression<Func<TFile, bool>> GetKeyPredicate(long id) => entity => entity.FileId == id;

    /// <inheritdoc />
    public override long GetKey(TFile entity) => entity.FileId;

    /// <summary>Thumbnail object path for an original path: <c>{size}/{path}</c>, PNG for GIF/BMP/ICO sources.</summary>
    /// <param name="size">A non-default size.</param>
    /// <param name="filePath">Original relative path.</param>
    /// <returns>The thumbnail's relative path (always <c>/</c>-separated).</returns>
    public static string ThumbnailPath(ImageSize size, string filePath)
    {
        ArgumentNullException.ThrowIfNull(filePath);
        var normalized = StoragePath.Normalize(filePath);
        var ext = Path.GetExtension(normalized);
        if (ext.Length > 0)
        {
            var thumbExt = ImageFormats.GetThumbnailExtension(ext);
            if (!string.Equals(ext, thumbExt, StringComparison.Ordinal))
            {
                normalized = normalized[..^ext.Length] + thumbExt;
            }
        }

        return StoragePath.Combine(size.ToString(), normalized);
    }

    #region Reads

    /// <inheritdoc />
    public async Task<FileResult> GetFile(long id, CancellationToken cancellationToken = default)
    {
        var file = await FindAsync(id, cancellationToken).ConfigureAwait(false) ?? throw NotFound(id);
        var stream = await _storageProvider
            .RetrieveAsync(file.FilePath, file.FileType.ToString(), cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            Logger.LogError("Content of file {FileId} is missing from storage", id);
            throw NotFound(id);
        }

        return new FileResult { Stream = stream, ContentType = ContentTypeMap.FromExtension(file.FileExtension) };
    }

    /// <inheritdoc />
    public async Task<FileResult> GetImage(long id, ImageSize size = ImageSize.Default,
        CancellationToken cancellationToken = default)
    {
        var file = await FindAsync(id, cancellationToken).ConfigureAwait(false) ?? throw NotFound(id);
        if (file.FileType != FileType.Image)
        {
            throw NotFound(id);
        }

        var container = FileType.Image.ToString();
        if (size == ImageSize.Default)
        {
            var original = await _storageProvider.RetrieveAsync(file.FilePath, container, cancellationToken)
                .ConfigureAwait(false);
            if (original is null)
            {
                throw NotFound(id);
            }

            return new FileResult { Stream = original, ContentType = ContentTypeMap.FromExtension(file.FileExtension) };
        }

        var thumbnailPath = ThumbnailPath(size, file.FilePath);
        var thumbnailType = ContentTypeMap.FromExtension(Path.GetExtension(thumbnailPath));
        var existing = await _storageProvider.RetrieveAsync(thumbnailPath, container, cancellationToken)
            .ConfigureAwait(false);
        if (existing is not null)
        {
            return new FileResult { Stream = existing, ContentType = thumbnailType };
        }

        // The thumbnail is missing: build it from the original, keep it for next time, and serve it.
        await using var source = await _storageProvider.RetrieveAsync(file.FilePath, container, cancellationToken)
            .ConfigureAwait(false) ?? throw NotFound(id);
        var bytes = ResizeToBytes(source, size, file.FilePath);
        try
        {
            await _storageProvider.StoreAsync(thumbnailPath, bytes, container).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Logger.LogWarning(e, "Could not cache the generated thumbnail of file {FileId}", id);
        }

        return new FileResult { Stream = new MemoryStream(bytes, writable: false), ContentType = thumbnailType };
    }

    /// <inheritdoc />
    public async Task<PageList<TFile>> ListAsync(FileListQuery query, Expression<Func<TFile, bool>>? scope = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        if (query.Page < 1 || query.PageSize < 1 || query.PageSize > FileListQuery.MaxPageSize)
        {
            throw new ApiException($"Page must be >= 1 and page size between 1 and {FileListQuery.MaxPageSize}.", null,
                LogLevel.Warning, null, HttpStatusCode.BadRequest, "INVALID_PAGING");
        }

        var filter = FileMetadata<TFile>.BuildFilter(query, scope);
        var total = await Repo.CountAsync(filter, flags: QueryFlags.DisableTracking, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var items = await Repo.GetAsync(filter, q => q.OrderByDescending(f => f.FileId), null,
            query.PageSize, (query.Page - 1) * query.PageSize, QueryFlags.DisableTracking, cancellationToken)
            .ConfigureAwait(false);
        return new PageList<TFile>(items.ToList(), total, query.PageSize, query.Page);
    }

    #endregion

    #region Create

    /// <summary>Not supported: a file row needs content. Use <see cref="Upload"/>.</summary>
    /// <param name="entity">Ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Never returns.</returns>
    /// <exception cref="ApiException">Always 400 <c>FILE_CONTENT_REQUIRED</c>.</exception>
    public override Task<TFile> Create(TFile entity, CancellationToken cancellationToken = default) =>
        throw new ApiException("Select a file to upload. Use Upload(metadata, content).", null, LogLevel.Warning, null,
            HttpStatusCode.BadRequest, "FILE_CONTENT_REQUIRED");

    /// <inheritdoc />
    public async Task<TFile> Upload(TFile metadata, FileUpload content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);

        var stored = new List<StoredObject>();
        try
        {
            var result = await StoreContentAsync(metadata.FileType, content, stored, cancellationToken)
                .ConfigureAwait(false);
            metadata.FileId = 0;
            ApplyStorageFields(metadata, result);
            FileMetadata<TFile>.EnsureRequiredStrings(metadata, result.OriginalFileName);

            if (!IsValidToCreate(metadata, out var error))
            {
                throw new ApiException($"Failed to create file. {error}", null, LogLevel.Warning, 2001,
                    HttpStatusCode.BadRequest, "FILE_INVALID");
            }

            await PreCreate(metadata, cancellationToken).ConfigureAwait(false);
            await Repo.InsertAsync(metadata, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CleanupAsync(stored).ConfigureAwait(false);
            throw;
        }

        await PostCreate(metadata, cancellationToken).ConfigureAwait(false);
        return metadata;
    }

    /// <inheritdoc />
    public async Task<TFile> Replace(long id, FileUpload content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        var file = await GetTrackedAsync(id, cancellationToken).ConfigureAwait(false);
        var oldPath = file.FilePath;
        var oldType = file.FileType;

        var stored = new List<StoredObject>();
        try
        {
            var result = await StoreContentAsync(file.FileType, content, stored, cancellationToken).ConfigureAwait(false);
            ApplyStorageFields(file, result);
            await Repo.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await CleanupAsync(stored).ConfigureAwait(false);
            throw;
        }

        // The row now points at the new objects; remove the old ones from the container of the OLD type.
        await RemoveObjectsAsync(oldType, oldPath).ConfigureAwait(false);
        await PostUpdate(file, cancellationToken).ConfigureAwait(false);
        return file;
    }

    #endregion

    #region Update

    /// <summary>Updates the allow-listed fields (title, comment, reference, duration, type, extra properties).</summary>
    /// <param name="id">File id.</param>
    /// <param name="entity">Client values; storage-owned fields are ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    public override async Task<TFile> Update(long id, TFile entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var current = await GetTrackedAsync(id, cancellationToken).ConfigureAwait(false);
        if (!IsValidToUpdate(current, out var error))
        {
            throw new ApiException($"Failed to update file. {error}", null, LogLevel.Warning, 4001,
                HttpStatusCode.BadRequest, "FILE_INVALID");
        }

        await PreUpdate(current, entity, cancellationToken).ConfigureAwait(false);
        FileMetadata<TFile>.CopyFrom(current, entity);
        return await FinishUpdateAsync(current, entity.FileType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Updates the allow-listed members present in the JSON document; every other member is ignored.</summary>
    /// <param name="id">File id.</param>
    /// <param name="entity">Partial update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    public override async Task<TFile> Update(long id, JsonObject entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var current = await GetTrackedAsync(id, cancellationToken).ConfigureAwait(false);
        if (!IsValidToUpdate(current, out var error))
        {
            throw new ApiException($"Failed to update file. {error}", null, LogLevel.Warning, 4002,
                HttpStatusCode.BadRequest, "FILE_INVALID");
        }

        await PreUpdate(current, entity, cancellationToken).ConfigureAwait(false);
        var requested = FileMetadata<TFile>.ApplyJson(current, entity);
        return await FinishUpdateAsync(current, requested ?? current.FileType, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>JSON-patch updates are limited to the same allow-list as other updates.</summary>
    /// <param name="id">File id.</param>
    /// <param name="entity">Patch document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    /// <exception cref="ApiException">An operation targets a storage-owned field (400).</exception>
    public override Task<TFile> Update(long id, JsonPatchDocument<TFile> entity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        foreach (var operation in entity.Operations)
        {
            var first = (operation.path ?? string.Empty).Trim('/').Split('/')[0];
            if (!FileMetadata<TFile>.AllowedNames.Contains(first, StringComparer.OrdinalIgnoreCase) ||
                operation.OperationType != Microsoft.AspNetCore.JsonPatch.Operations.OperationType.Replace &&
                operation.OperationType != Microsoft.AspNetCore.JsonPatch.Operations.OperationType.Add)
            {
                throw new ApiException($"The field '{first}' cannot be changed by an update.", null, LogLevel.Warning,
                    null, HttpStatusCode.BadRequest, "FIELD_NOT_UPDATABLE");
            }

            if (string.Equals(first, nameof(IFile.FileType), StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException("Change the file type with a full or JSON update.", null, LogLevel.Warning, null,
                    HttpStatusCode.BadRequest, "FIELD_NOT_UPDATABLE");
            }
        }

        return base.Update(id, entity, cancellationToken);
    }

    private async Task<TFile> FinishUpdateAsync(TFile current, FileType newType, CancellationToken ct)
    {
        var oldType = current.FileType;
        var created = new List<StoredObject>();
        try
        {
            if (newType != oldType)
            {
                await RelocateAsync(current, oldType, newType, created, ct).ConfigureAwait(false);
                current.FileType = newType;
            }

            await Repo.UpdateAsync(current, ct).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            current.FileType = oldType;
            await CleanupAsync(created).ConfigureAwait(false);
            throw;
        }

        if (newType != oldType)
        {
            // The old objects live in the container of the OLD type (V-4).
            await RemoveObjectsAsync(oldType, current.FilePath).ConfigureAwait(false);
        }

        await PostUpdate(current, ct).ConfigureAwait(false);
        return current;
    }

    // Copies the object to the container of the new type (and builds or drops thumbnails) before the row changes.
    private async Task RelocateAsync(TFile file, FileType oldType, FileType newType, List<StoredObject> created,
        CancellationToken ct)
    {
        await using var source = await _storageProvider.RetrieveAsync(file.FilePath, oldType.ToString(), ct)
            .ConfigureAwait(false) ?? throw NotFound(file.FileId);
        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        await _storageProvider.StoreAsync(file.FilePath, bytes, newType.ToString()).ConfigureAwait(false);
        created.Add(new StoredObject(newType.ToString(), file.FilePath));
        if (newType == FileType.Image)
        {
            await StoreThumbnailsAsync(bytes, file.FilePath, created, ct).ConfigureAwait(false);
        }
    }

    #endregion

    #region Delete

    /// <summary>Hard-deletes a file: removes the row, then its stored objects and thumbnails.</summary>
    /// <param name="id">File id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deleted entity.</returns>
    public override async Task<TFile> Delete(long id, CancellationToken cancellationToken = default)
    {
        var file = await GetTrackedAsync(id, cancellationToken).ConfigureAwait(false);
        if (!IsValidToDelete(file, out var error))
        {
            throw new ApiException($"Can't delete file {id}. {error}", null, LogLevel.Warning, null,
                HttpStatusCode.BadRequest, "FILE_DELETE_REJECTED");
        }

        await PreDelete(file, cancellationToken).ConfigureAwait(false);
        Repo.Delete(file);
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await RemoveObjectsAsync(file.FileType, file.FilePath).ConfigureAwait(false);
        await PostDelete(file, cancellationToken).ConfigureAwait(false);
        return file;
    }

    /// <summary>Hard-deletes several files; stored objects are removed after the rows.</summary>
    /// <param name="ids">File ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The deleted entities.</returns>
    public override async Task<IEnumerable<TFile>> Delete(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var idList = ids.ToList();
        var files = (await Repo.GetAsync(f => idList.Contains(f.FileId), cancellationToken: cancellationToken)
            .ConfigureAwait(false)).ToList();
        await PreDelete(files, cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            Repo.Delete(file);
        }

        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            await RemoveObjectsAsync(file.FileType, file.FilePath).ConfigureAwait(false);
        }

        await PostDelete(files, cancellationToken).ConfigureAwait(false);
        return files;
    }

    /// <inheritdoc />
    public async Task Delete(Expression<Func<TFile, bool>> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var files = (await Repo.GetAsync(query, cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        foreach (var file in files)
        {
            Repo.Delete(file);
        }

        // Rows first: if the save fails the stored objects are untouched.
        await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            await RemoveObjectsAsync(file.FileType, file.FilePath).ConfigureAwait(false);
        }
    }

    #endregion

    private sealed record StoredObject(string Container, string Path);

    private sealed record StoredContent(
        string FilePath, long Size, string Extension, string OriginalFileName, string OriginalMediaType);

    private async Task<TFile?> FindAsync(long id, CancellationToken ct) =>
        await Repo.FirstOrDefaultAsync(GetKeyPredicate(id), flags: QueryFlags.DisableTracking, cancellationToken: ct)
            .ConfigureAwait(false);

    private async Task<TFile> GetTrackedAsync(long id, CancellationToken ct) =>
        await Repo.FirstOrDefaultAsync(GetKeyPredicate(id), cancellationToken: ct).ConfigureAwait(false)
        ?? throw NotFound(id);

    private static ApiException NotFound(long id) =>
        new($"File {id} not found.", null, LogLevel.Warning, null, HttpStatusCode.NotFound, "FILE_NOT_FOUND");

    private static void ApplyStorageFields(TFile file, StoredContent stored)
    {
        file.FilePath = stored.FilePath;
        file.FileSize = stored.Size;
        file.FileExtension = stored.Extension;
        file.OriginalFileName = stored.OriginalFileName;
        file.OriginalMediaType = stored.OriginalMediaType;
    }

    // Validates the upload, stores the original (and thumbnails for images) and records every object it wrote.
    private async Task<StoredContent> StoreContentAsync(FileType type, FileUpload content, List<StoredObject> stored,
        CancellationToken ct)
    {
        var fileName = SanitizeFileName(content.FileName);
        var extension = ValidateExtension(fileName);
        if (_options.MaxFileSizeBytes is { } max && content.Length is { } declared && declared > max)
        {
            throw CountingStream.TooLarge(max);
        }

        var container = type.ToString();
        var path = $"{_timeProvider.GetUtcNow():yyyyMMdd}/{Guid.NewGuid():N}{extension}";
        var writeOptions = new StorageWriteOptions { ContentType = ContentTypeMap.FromExtension(extension) };

        await using var source = content.OpenReadStream();
        await using var counting = new CountingStream(source, _options.MaxFileSizeBytes);
        long size;
        if (type == FileType.Image)
        {
            // Thumbnails need a seekable copy; build them first so a bad image is rejected before anything is stored.
            using var buffer = new MemoryStream();
            await counting.CopyToAsync(buffer, ct).ConfigureAwait(false);
            size = buffer.Length;
            var bytes = buffer.ToArray();
            var thumbnails = ThumbnailSizes.ToDictionary(s => s, s => ResizeToBytes(new MemoryStream(bytes), s, path));

            using var original = new MemoryStream(bytes, writable: false);
            await _storageProvider.StoreAsync(original, path, writeOptions, container, ct).ConfigureAwait(false);
            stored.Add(new StoredObject(container, path));
            foreach (var (thumbSize, data) in thumbnails)
            {
                var thumbPath = ThumbnailPath(thumbSize, path);
                using var thumbStream = new MemoryStream(data, writable: false);
                await _storageProvider.StoreAsync(thumbStream, thumbPath,
                    new StorageWriteOptions { ContentType = ContentTypeMap.FromExtension(Path.GetExtension(thumbPath)) },
                    container, ct).ConfigureAwait(false);
                stored.Add(new StoredObject(container, thumbPath));
            }
        }
        else
        {
            await _storageProvider.StoreAsync(counting, path, writeOptions, container, ct).ConfigureAwait(false);
            stored.Add(new StoredObject(container, path));
            size = counting.BytesRead;
        }

        var mediaType = content.ContentType ?? string.Empty;
        if (mediaType.Length > 500)
        {
            mediaType = mediaType[..500];
        }

        return new StoredContent(path, size, extension, fileName, mediaType);
    }

    private async Task StoreThumbnailsAsync(byte[] original, string filePath, List<StoredObject> created, CancellationToken ct)
    {
        var container = FileType.Image.ToString();
        foreach (var size in ThumbnailSizes)
        {
            var data = ResizeToBytes(new MemoryStream(original), size, filePath);
            var thumbPath = ThumbnailPath(size, filePath);
            await _storageProvider.StoreAsync(thumbPath, data, container).ConfigureAwait(false);
            created.Add(new StoredObject(container, thumbPath));
        }
    }

    private byte[] ResizeToBytes(Stream source, ImageSize size, string filePath)
    {
        using (source)
        using (var output = new MemoryStream())
        {
            _imageService.Resize(source, size.GetSize(), output, Path.GetExtension(filePath), true, null);
            return output.ToArray();
        }
    }

    private string ValidateExtension(string fileName)
    {
        var extension = Path.GetExtension(fileName);
        if (extension.Length > StorageServiceOptions.MaxExtensionLength ||
            extension.Skip(1).Any(c => !char.IsAsciiLetterOrDigit(c)))
        {
            throw Rejected("FILE_EXTENSION_INVALID", $"The extension '{extension}' is not valid.");
        }

        var bare = extension.TrimStart('.');
        if (_options.DeniedExtensions.Any(d => string.Equals(d.TrimStart('.'), bare, StringComparison.OrdinalIgnoreCase)))
        {
            throw Rejected("FILE_EXTENSION_DENIED", $"Files with the extension '{extension}' are not accepted.");
        }

        if (_options.AllowedExtensions.Count > 0 && !_options.AllowedExtensions.Any(a =>
                string.Equals(a.TrimStart('.'), bare, StringComparison.OrdinalIgnoreCase)))
        {
            throw Rejected("FILE_EXTENSION_NOT_ALLOWED", $"Files with the extension '{extension}' are not accepted.");
        }

        return extension;
    }

    private static ApiException Rejected(string code, string message) =>
        new(message, null, LogLevel.Warning, null, HttpStatusCode.BadRequest, code);

    // Reduces a client name to a plain file name: no directories, no control characters, at most 500 characters.
    private static string SanitizeFileName(string name)
    {
        var unified = (name ?? string.Empty).Replace('\\', '/');
        unified = unified[(unified.LastIndexOf('/') + 1)..];
        var builder = new StringBuilder(unified.Length);
        foreach (var c in unified)
        {
            if (c >= ' ' && c != '\u007f')
            {
                builder.Append(c);
            }
        }

        var clean = builder.ToString().Trim();
        if (clean.Length == 0)
        {
            return "file";
        }

        if (clean.Length > 500)
        {
            var ext = Path.GetExtension(clean);
            clean = ext.Length is > 0 and <= StorageServiceOptions.MaxExtensionLength
                ? clean[..(500 - ext.Length)] + ext
                : clean[..500];
        }

        return clean;
    }

    // Best effort: failures are logged, never thrown (the row is already consistent).
    private async Task RemoveObjectsAsync(FileType type, string filePath)
    {
        var container = type.ToString();
        await TryDeleteAsync(filePath, container).ConfigureAwait(false);
        if (type == FileType.Image)
        {
            foreach (var size in ThumbnailSizes)
            {
                await TryDeleteAsync(ThumbnailPath(size, filePath), container).ConfigureAwait(false);
            }
        }
    }

    private async Task CleanupAsync(IEnumerable<StoredObject> stored)
    {
        foreach (var item in stored)
        {
            await TryDeleteAsync(item.Path, item.Container).ConfigureAwait(false);
        }
    }

    private async Task TryDeleteAsync(string path, string container)
    {
        try
        {
            await _storageProvider.DeleteAsync(path, container, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            Logger.LogWarning(e, "Could not remove stored object from container {Container}", container);
        }
    }
}
