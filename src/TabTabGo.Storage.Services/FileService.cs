using System.Globalization;
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
// Any subclass inherits those mismatches at the class declaration (reported to Core). The suppression covers the
// declaration line only; the one overridden member that needs it (Create) has its own scope.
#pragma warning disable CS8767, CS8614

/// <summary>
/// Upload, replace, update, read and delete for file metadata and content. Storage-owned fields (path, size,
/// extension, original name and media type) change only through <see cref="Upload"/> and <see cref="Replace"/>;
/// client updates are limited to an allow-list.
/// </summary>
/// <typeparam name="TFile">File entity type.</typeparam>
public class FileService<TFile> : BaseService<TFile, long>, IFileService<TFile> where TFile : class, IFile
#pragma warning restore CS8767, CS8614
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
    /// <remarks>
    /// Includes <c>IsEnabled</c>: "delete" means "hide", so every inherited by-key read (<c>GetByKey</c>,
    /// <c>Exists</c>, <c>GetViewModelByKey</c>) skips soft-deleted files. <c>Purge</c> looks files up without this filter.
    /// </remarks>
    public override Expression<Func<TFile, bool>> GetKeyPredicate(long id) => entity => entity.FileId == id && entity.IsEnabled;

    private static Expression<Func<TFile, bool>> KeyOnly(long id) => entity => entity.FileId == id;

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

    /// <summary>Core list read, limited to files that are not soft-deleted.</summary>
#pragma warning disable CS8765, CS8767, CS8620, CS8604
    public override Task<IEnumerable<TFile>> GetList(Expression<Func<TFile?, bool>> query, string[]? includeProperties = null,
        QueryFlags? flags = null, CancellationToken cancellationToken = default) =>
        base.GetList(ActiveFilter.And<TFile>(query!)!, includeProperties, flags, cancellationToken);

    /// <summary>Core paged read, limited to files that are not soft-deleted.</summary>
    public override Task<PageList<TFile>> GetPageList(Expression<Func<TFile?, bool>> query,
        Func<IQueryable<TFile>, IOrderedQueryable<TFile>>? orderBy = null, int page = 0, int pageSize = 25,
        string[]? includeProperties = null, QueryFlags? flags = null, CancellationToken cancellationToken = default) =>
        base.GetPageList(ActiveFilter.And<TFile>(query!)!, orderBy, page, pageSize, includeProperties, flags, cancellationToken);

    /// <summary>OData read behind <c>GetPageList(odata)</c> and <c>GetViewModels</c>, limited to files that are not soft-deleted.</summary>
    public override IQueryable<TFile?> GetQueryable(Microsoft.AspNetCore.OData.Query.ODataQueryOptions<TFile> query,
        out int pageSize, out int skip, out int pageNumber, string[]? includeProperties = null,
        Expression<Func<TFile, bool>>? fixCriteria = null, QueryFlags? flags = null) =>
        base.GetQueryable(query, out pageSize, out skip, out pageNumber, includeProperties, ActiveFilter.And(fixCriteria), flags);
#pragma warning restore CS8765, CS8767, CS8620, CS8604

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
            using var cache = new MemoryStream(bytes, writable: false);
            await _storageProvider.StoreAsync(cache, thumbnailPath, new StorageWriteOptions { ContentType = thumbnailType },
                container, cancellationToken).ConfigureAwait(false);
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
#pragma warning disable CS8765, CS8767
    public override Task<TFile> Create(TFile entity, CancellationToken cancellationToken = default) =>
#pragma warning restore CS8765, CS8767
        throw new ApiException("Select a file to upload. Use Upload(metadata, content).", null, LogLevel.Warning, null,
            HttpStatusCode.BadRequest, "FILE_CONTENT_REQUIRED");

    /// <remarks>
    /// <c>IsValidToCreate</c> and <c>PreCreate</c> run <b>before</b> the content is stored, so a veto stores nothing; the
    /// storage fields (path, size, extension, original name and media type) are not populated yet - use
    /// <c>PostCreate</c> for logic that needs them. Audit fields (<c>CreatedBy</c>, <c>UpdatedBy</c>) and
    /// <c>ExtraProperties</c> are taken from <paramref name="metadata"/>: build it from the authenticated user in
    /// server code and never bind it straight from a request body. <c>IsEnabled</c>, dates and <c>IsCompressed</c> are reset.
    /// </remarks>
    /// <inheritdoc />
    public async Task<TFile> Upload(TFile metadata, FileUpload content, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(content);
        EnsureDefined(metadata.FileType);
        var prepared = Prepare(content);

        metadata.FileId = 0;
        metadata.IsEnabled = true;
        metadata.CreatedDate = _timeProvider.GetUtcNow();
        metadata.UpdatedDate = default;
        // We never compress what we store, so false is the truthful value (null would pick the column default, true).
        metadata.IsCompressed = false;
        if (!_options.AllowMetadataExtraProperties)
        {
            metadata.ExtraProperties = new Dictionary<string, object>();
        }
        else
        {
            EnsureExtraPropertiesWithinLimits(metadata.ExtraProperties);
        }

        // The hooks below judge what is really being stored, not what the metadata claims.
        metadata.FileExtension = prepared.Extension;
        metadata.OriginalFileName = prepared.FileName;
        metadata.FileSize = content.Length;
        metadata.FilePath = string.Empty;
        FileMetadata<TFile>.EnsureRequiredStrings(metadata, prepared.FileName);

        if (!IsValidToCreate(metadata, out var error))
        {
            throw new ApiException($"Failed to create file. {error}", null, LogLevel.Warning, 2001,
                HttpStatusCode.BadRequest, "FILE_INVALID");
        }

        await PreCreate(metadata, cancellationToken).ConfigureAwait(false);

        var stored = new List<StoredObject>();
        try
        {
            var result = await StoreContentAsync(metadata.FileType, content, prepared, stored, cancellationToken)
                .ConfigureAwait(false);
            ApplyStorageFields(metadata, result);
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
        var file = await LoadTrackedAsync(id, includeDisabled: false, cancellationToken).ConfigureAwait(false);
        EnsureValidToUpdate(file, _ => { }, 4001);

        await PreUpdate(file, file, cancellationToken).ConfigureAwait(false);
        var prepared = Prepare(content);
        var oldPath = file.FilePath;
        var oldType = file.FileType;
        var previous = new StoredContent(file.FilePath, file.FileSize, file.FileExtension, file.OriginalFileName,
            file.OriginalMediaType);
        var previousUpdated = file.UpdatedDate;

        var stored = new List<StoredObject>();
        try
        {
            var result = await StoreContentAsync(file.FileType, content, prepared, stored, cancellationToken)
                .ConfigureAwait(false);
            ApplyStorageFields(file, result);
            file.UpdatedDate = _timeProvider.GetUtcNow();
            await Repo.UpdateAsync(file, cancellationToken).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The row still points at the old objects; keep the in-memory entity consistent with that.
            ApplyStorageFields(file, previous);
            file.UpdatedDate = previousUpdated;
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
    /// <remarks>
    /// <c>FileType</c> left at <c>Unknown</c> (a DTO that omits it) means "unchanged"; an undefined value is a 400.
    /// <c>IsValidToUpdate</c> receives the incoming entity, as in Core's <c>BaseService</c>.
    /// </remarks>
    /// <param name="id">File id.</param>
    /// <param name="entity">Client values; storage-owned fields are ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    public override async Task<TFile> Update(long id, TFile entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        // The caller may pass the tracked instance itself (load, edit, update): take its values before it is restored.
        var incomingCopy = (TFile)MemberwiseCloneMethod.Invoke(entity, null)!;
        incomingCopy.ExtraProperties = entity.ExtraProperties is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(entity.ExtraProperties);
        entity = incomingCopy;
        var current = await LoadTrackedAsync(id, includeDisabled: false, cancellationToken).ConfigureAwait(false);
        EnsureDefined(entity.FileType);
        EnsureValidToUpdate(current, merged => FileMetadata<TFile>.CopyFrom(merged, entity, _options.AllowMetadataExtraProperties), 4001);

        await PreUpdate(current, entity, cancellationToken).ConfigureAwait(false);
        var undo = FileMetadata<TFile>.Snapshot(current);
        try
        {
            FileMetadata<TFile>.CopyFrom(current, entity, _options.AllowMetadataExtraProperties);
            var newType = entity.FileType == FileType.Unknown ? current.FileType : entity.FileType;
            return await FinishUpdateAsync(current, newType, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            undo();
            throw;
        }
    }

    /// <summary>Updates the allow-listed members present in the JSON document; every other member is ignored.</summary>
    /// <param name="id">File id.</param>
    /// <param name="entity">Partial update.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    public override async Task<TFile> Update(long id, JsonObject entity, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entity);
        var current = await LoadTrackedAsync(id, includeDisabled: false, cancellationToken).ConfigureAwait(false);
        var requested = FileMetadata<TFile>.ReadFileType(entity);
        if (requested is { } type)
        {
            EnsureDefined(type);
        }

        EnsureValidToUpdate(current, merged => FileMetadata<TFile>.ApplyJson(merged, entity, _options.AllowMetadataExtraProperties), 4002);

        await PreUpdate(current, entity, cancellationToken).ConfigureAwait(false);
        var undo = FileMetadata<TFile>.Snapshot(current);
        try
        {
            FileMetadata<TFile>.ApplyJson(current, entity, _options.AllowMetadataExtraProperties);
            var newType = requested is null or FileType.Unknown ? current.FileType : requested.Value;
            return await FinishUpdateAsync(current, newType, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            undo();
            throw;
        }
    }

    /// <summary>JSON-patch updates are limited to the same allow-list as other updates.</summary>
    /// <param name="id">File id.</param>
    /// <param name="entity">Patch document.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    /// <exception cref="ApiException">An operation targets a storage-owned field (400).</exception>
    public override async Task<TFile> Update(long id, JsonPatchDocument<TFile> entity,
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

            if (!_options.AllowMetadataExtraProperties &&
                string.Equals(first, nameof(IFile.ExtraProperties), StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException("ExtraProperties cannot be changed by an update (StorageServiceOptions.AllowMetadataExtraProperties is off).",
                    null, LogLevel.Warning, null, HttpStatusCode.BadRequest, "FIELD_NOT_UPDATABLE");
            }

            if (string.Equals(first, nameof(IFile.FileType), StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException("Change the file type with a full or JSON update.", null, LogLevel.Warning, null,
                    HttpStatusCode.BadRequest, "FIELD_NOT_UPDATABLE");
            }
        }

        var current = await LoadTrackedAsync(id, includeDisabled: false, cancellationToken).ConfigureAwait(false);
        EnsureValidToUpdate(current, merged => entity.ApplyTo(merged, PatchFailure), 4002);

        await PreUpdate(current, entity, cancellationToken).ConfigureAwait(false);
        var undo = FileMetadata<TFile>.Snapshot(current);
        try
        {
            entity.ApplyTo(current, patchError => throw new ApiException(patchError.ErrorMessage, null, LogLevel.Warning,
                null, HttpStatusCode.BadRequest, "INVALID_PATCH"));
            return await FinishUpdateAsync(current, current.FileType, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            undo();
            throw;
        }
    }

    /// <summary>
    /// Routed through <see cref="Update(long, TFile, CancellationToken)"/>: the base implementation saves whatever the
    /// tracked entity holds, which would bypass the allow-list.
    /// </summary>
    /// <param name="id">File id.</param>
    /// <param name="entity">Client values; storage-owned fields are ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The updated entity.</returns>
    public override Task<TFile> UpdateChanges(long id, TFile entity, CancellationToken cancellationToken) =>
        Update(id, entity, cancellationToken);

    /// <summary>
    /// The update gate. It must hold in <b>both</b> states: the stored row (<paramref name="current"/>: database truth,
    /// so a foreign tenant's or owner's record is refused whatever the client sends) and the row after the allow-listed
    /// client changes (<paramref name="merged"/>: so an update cannot move a record out of, or into, a scope the caller
    /// may not touch). The default calls <c>IsValidToUpdate(entity)</c> for each; override it to decide on both at once.
    /// </summary>
    /// <param name="current">The stored row.</param>
    /// <param name="merged">The stored row with the allow-listed client changes applied.</param>
    /// <param name="validationErrorMessage">Why the update is refused.</param>
    /// <returns><c>true</c> when the update may proceed.</returns>
    public virtual bool IsValidToUpdate(TFile current, TFile merged, out string validationErrorMessage) =>
        IsValidToUpdate(current, out validationErrorMessage) && IsValidToUpdate(merged, out validationErrorMessage);

    // Bounds the ExtraProperties bag (key count and serialized size) so a request body cannot grow a row without limit.
    private void EnsureExtraPropertiesWithinLimits(IDictionary<string, object>? bag)
    {
        if (bag is null || bag.Count == 0)
        {
            return;
        }

        if (bag.Count > _options.MaxExtraPropertyKeys ||
            System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(bag).Length > _options.MaxExtraPropertiesBytes)
        {
            throw new ApiException(
                $"ExtraProperties may hold at most {_options.MaxExtraPropertyKeys} keys and {_options.MaxExtraPropertiesBytes} bytes.",
                null, LogLevel.Warning, null, HttpStatusCode.BadRequest, "EXTRA_PROPERTIES_TOO_LARGE");
        }
    }

    private static void PatchFailure(Microsoft.AspNetCore.JsonPatch.JsonPatchError error) =>
        throw new ApiException(error.ErrorMessage, null, LogLevel.Warning, null, HttpStatusCode.BadRequest, "INVALID_PATCH");

    // The update gate judges the MERGED entity: a copy of the row loaded by id (database truth for identity, ownership,
    // audit and storage-owned fields) with only the allow-listed client changes applied. Client-claimed values for the
    // other fields can therefore never satisfy an authorization or state check.
    private void EnsureValidToUpdate(TFile current, Action<TFile> applyClientChanges, int errorNumber)
    {
        var merged = (TFile)MemberwiseCloneMethod.Invoke(current, null)!;
        merged.ExtraProperties = current.ExtraProperties is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(current.ExtraProperties);
        applyClientChanges(merged);
        if (_options.AllowMetadataExtraProperties)
        {
            EnsureExtraPropertiesWithinLimits(merged.ExtraProperties);
        }

        if (!IsValidToUpdate(current, merged, out var error))
        {
            throw new ApiException($"Failed to update file. {error}", null, LogLevel.Warning, errorNumber,
                HttpStatusCode.BadRequest, "FILE_INVALID");
        }
    }

    private static readonly System.Reflection.MethodInfo MemberwiseCloneMethod =
        typeof(object).GetMethod("MemberwiseClone", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;

    private async Task<TFile> FinishUpdateAsync(TFile current, FileType newType, CancellationToken ct)
    {
        var oldType = current.FileType;
        var oldUpdated = current.UpdatedDate;
        var created = new List<StoredObject>();
        try
        {
            if (newType != oldType)
            {
                await RelocateAsync(current, oldType, newType, created, ct).ConfigureAwait(false);
                current.FileType = newType;
            }

            // UpdatedBy is the host's (set it on `current` in PreUpdate, or in a SaveChanges interceptor).
            current.UpdatedDate = _timeProvider.GetUtcNow();

            await Repo.UpdateAsync(current, ct).ConfigureAwait(false);
            await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            current.FileType = oldType;
            current.UpdatedDate = oldUpdated;
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
    // Non-images are streamed; images are buffered once because the thumbnails are built before anything is stored.
    private async Task RelocateAsync(TFile file, FileType oldType, FileType newType, List<StoredObject> created,
        CancellationToken ct)
    {
        var newContainer = newType.ToString();
        var options = new StorageWriteOptions { ContentType = ContentTypeMap.FromExtension(file.FileExtension) };
        await using var source = await _storageProvider.OpenReadAsync(file.FilePath, oldType.ToString(), ct)
            .ConfigureAwait(false) ?? throw NotFound(file.FileId);

        if (newType != FileType.Image)
        {
            await _storageProvider.StoreAsync(source, file.FilePath, options, newContainer, ct).ConfigureAwait(false);
            created.Add(new StoredObject(newContainer, file.FilePath));
            return;
        }

        using var buffer = new MemoryStream();
        await source.CopyToAsync(buffer, ct).ConfigureAwait(false);
        var bytes = buffer.GetBuffer();
        var length = (int)buffer.Length;
        var thumbnails = ThumbnailSizes.ToDictionary(s => s, s => ResizeToBytes(new MemoryStream(bytes, 0, length, false), s, file.FilePath));

        using var original = new MemoryStream(bytes, 0, length, false);
        await _storageProvider.StoreAsync(original, file.FilePath, options, newContainer, ct).ConfigureAwait(false);
        created.Add(new StoredObject(newContainer, file.FilePath));
        await StoreThumbnailsAsync(thumbnails, file.FilePath, newContainer, created, ct).ConfigureAwait(false);
    }

    #endregion

    #region Delete and purge

    /// <summary>
    /// Soft-deletes a file (<c>IsEnabled = false</c>), as Core and 6.x do. The stored objects are kept; use
    /// <see cref="Purge(long, CancellationToken)"/> to remove them.
    /// </summary>
    /// <param name="id">File id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The disabled entity.</returns>
    public override async Task<TFile> Delete(long id, CancellationToken cancellationToken = default)
    {
        var file = await LoadTrackedAsync(id, includeDisabled: false, cancellationToken).ConfigureAwait(false);
        EnsureAllDeletable([file]);
        await PreDelete(file, cancellationToken).ConfigureAwait(false);
        var previousUpdated = file.UpdatedDate;
        file.IsEnabled = false;
        file.UpdatedDate = _timeProvider.GetUtcNow();
        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The row is unchanged; keep the in-memory entity consistent with it.
            file.IsEnabled = true;
            file.UpdatedDate = previousUpdated;
            throw;
        }

        await PostDelete(file, cancellationToken).ConfigureAwait(false);
        return file;
    }

    /// <summary>Soft-deletes several files (already disabled ones are skipped). The stored objects are kept.</summary>
    /// <param name="ids">File ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The files that were disabled.</returns>
    public override async Task<IEnumerable<TFile>> Delete(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var idList = ids.ToList();
        var files = await LoadManyAsync(f => idList.Contains(f.FileId) && f.IsEnabled, cancellationToken)
            .ConfigureAwait(false);
        EnsureAllDeletable(files);
        await RunPreDeleteAsync(files, bulk: true, cancellationToken).ConfigureAwait(false);
        var previous = files.ToDictionary(f => f.FileId, f => f.UpdatedDate);
        foreach (var file in files)
        {
            file.IsEnabled = false;
            file.UpdatedDate = _timeProvider.GetUtcNow();
        }

        try
        {
            await _unitOfWork.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var file in files)
            {
                file.IsEnabled = true;
                file.UpdatedDate = previous[file.FileId];
            }

            throw;
        }

        await RunPostDeleteAsync(files, bulk: true, cancellationToken).ConfigureAwait(false);
        return files;
    }

    /// <summary>Hard-deletes the matching rows (as in 6.x), then their stored objects and thumbnails.</summary>
    /// <param name="query">Filter selecting the files.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task that completes when the rows and objects are removed.</returns>
    public async Task Delete(Expression<Func<TFile, bool>> query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);
        var files = await LoadManyAsync(query, cancellationToken).ConfigureAwait(false);
        await HardDeleteAsync(files, bulk: true, purge: false, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Hard-deletes one file, soft-deleted or not: the row, then its stored objects and thumbnails. Not part of
    /// <see cref="IFileService{TFile}"/>: it is deliberately explicit.
    /// </summary>
    /// <param name="id">File id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The purged entity.</returns>
    public async Task<TFile> Purge(long id, CancellationToken cancellationToken = default)
    {
        var file = await LoadTrackedAsync(id, includeDisabled: true, cancellationToken).ConfigureAwait(false);
        await HardDeleteAsync([file], bulk: false, purge: true, cancellationToken).ConfigureAwait(false);
        return file;
    }

    /// <summary>Hard-deletes several files, soft-deleted or not: the rows, then their stored objects and thumbnails.</summary>
    /// <param name="ids">File ids; ids that do not exist are ignored.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The purged entities.</returns>
    public async Task<IEnumerable<TFile>> Purge(IEnumerable<long> ids, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var idList = ids.ToList();
        var files = await LoadManyAsync(f => idList.Contains(f.FileId), cancellationToken).ConfigureAwait(false);
        await HardDeleteAsync(files, bulk: true, purge: true, cancellationToken).ConfigureAwait(false);
        return files;
    }

    // Rows first: if the save fails the stored objects are untouched. Object paths come from the restored (database) values.
    private async Task HardDeleteAsync(List<TFile> files, bool bulk, bool purge, CancellationToken ct)
    {
        EnsureAllDeletable(files, purge);
        await RunPreDeleteAsync(files, bulk, ct).ConfigureAwait(false);
        var objects = files.Select(f => (f.FileType, f.FilePath)).ToList();
        foreach (var file in files)
        {
            Repo.Delete(file);
        }

        await _unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        foreach (var (type, path) in objects)
        {
            await RemoveObjectsAsync(type, path).ConfigureAwait(false);
        }

        await RunPostDeleteAsync(files, bulk, ct).ConfigureAwait(false);
    }

    // Single deletes call the single-entity hooks; bulk deletes call the collection hook and then fan out to the
    // single-entity hooks, so a host that only overrides PreDelete(file) / PostDelete(file) sees every path.
    private async Task RunPreDeleteAsync(List<TFile> files, bool bulk, CancellationToken ct)
    {
        if (bulk)
        {
            await PreDelete(files, ct).ConfigureAwait(false);
        }

        foreach (var file in files)
        {
            await PreDelete(file, ct).ConfigureAwait(false);
        }
    }

    private async Task RunPostDeleteAsync(List<TFile> files, bool bulk, CancellationToken ct)
    {
        if (bulk)
        {
            await PostDelete(files, ct).ConfigureAwait(false);
        }

        foreach (var file in files)
        {
            await PostDelete(file, ct).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Validation hook for <see cref="Purge(long, CancellationToken)"/> and its bulk form. Defaults to
    /// <c>IsValidToDelete</c>; override it to treat the irreversible purge more strictly than the soft delete.
    /// </summary>
    /// <param name="entity">The file about to be purged.</param>
    /// <param name="validationErrorMessage">Why the purge is refused.</param>
    /// <returns><c>true</c> when the purge may proceed.</returns>
    public virtual bool IsValidToPurge(TFile entity, out string validationErrorMessage) =>
        IsValidToDelete(entity, out validationErrorMessage);

    /// <summary>Whether the file exists, is not soft-deleted, and may be deleted. A missing or disabled file is <c>false</c>.</summary>
    /// <param name="id">File id.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when <see cref="Delete(long, CancellationToken)"/> would be allowed.</returns>
    public override async Task<bool> CanDelete(long id, CancellationToken cancellationToken = default)
    {
        var file = await FindAsync(id, cancellationToken).ConfigureAwait(false);
        return file is not null && IsValidToDelete(file, out _);
    }

    /// <summary>True only when every id exists, is not soft-deleted, and may be deleted.</summary>
    /// <param name="ids">File ids.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns><c>true</c> when <c>Delete(ids)</c> would delete all of them.</returns>
    public override async Task<bool> CanDelete(IEnumerable<long> ids, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        var idList = ids.Distinct().ToList();
        var files = (await Repo.GetAsync(f => idList.Contains(f.FileId) && f.IsEnabled, flags: QueryFlags.DisableTracking,
            cancellationToken: cancellationToken).ConfigureAwait(false)).ToList();
        return files.Count == idList.Count && files.All(f => IsValidToDelete(f, out _));
    }

    // Every delete overload rejects the whole operation when any file fails IsValidToDelete, before anything changes.
    private void EnsureAllDeletable(IEnumerable<TFile> files, bool purge = false)
    {
        foreach (var file in files)
        {
            string error;
            if (purge ? !IsValidToPurge(file, out error) : !IsValidToDelete(file, out error))
            {
                throw new ApiException($"Can't delete file {file.FileId}. {error}", null, LogLevel.Warning, null,
                    HttpStatusCode.BadRequest, "FILE_DELETE_REJECTED");
            }
        }
    }

    #endregion

    private sealed record StoredObject(string Container, string Path);

    private sealed record StoredContent(
        string FilePath, long? Size, string Extension, string OriginalFileName, string OriginalMediaType);

    private sealed record PreparedUpload(string FileName, string Extension);

    private static void EnsureDefined(FileType type)
    {
        if (!Enum.IsDefined(type))
        {
            throw new ApiException($"File type {(int)type} is not valid.", null, LogLevel.Warning, null,
                HttpStatusCode.BadRequest, "INVALID_FILE_TYPE");
        }
    }

    // Read-only lookup of the database truth (never the tracked instance, which a caller may have edited).
    private async Task<TFile?> FindAsync(long id, CancellationToken ct) =>
        await Repo.FirstOrDefaultAsync(GetKeyPredicate(id), flags: QueryFlags.DisableTracking, cancellationToken: ct)
            .ConfigureAwait(false);

    // The tracked entity to mutate. Storage-owned and audit fields are restored from the database first, so unsaved
    // edits made earlier on the same context can never be persisted or used to address another file's objects.
    private async Task<TFile> LoadTrackedAsync(long id, bool includeDisabled, CancellationToken ct)
    {
        var key = includeDisabled ? KeyOnly(id) : GetKeyPredicate(id);
        var tracked = await Repo.FirstOrDefaultAsync(key, cancellationToken: ct).ConfigureAwait(false) ?? throw NotFound(id);
        var truth = await Repo.FirstOrDefaultAsync(key, flags: QueryFlags.DisableTracking, cancellationToken: ct)
            .ConfigureAwait(false) ?? throw NotFound(id);
        RestoreOwned(tracked, truth);
        EnsureNoPendingNavigationChange(tracked, truth);
        return tracked;
    }

    private async Task<List<TFile>> LoadManyAsync(Expression<Func<TFile, bool>> filter, CancellationToken ct)
    {
        var tracked = (await Repo.GetAsync(filter, cancellationToken: ct).ConfigureAwait(false)).ToList();
        if (tracked.Count == 0)
        {
            return tracked;
        }

        var ids = tracked.Select(f => f.FileId).ToList();
        var truth = (await Repo.GetAsync(f => ids.Contains(f.FileId), flags: QueryFlags.DisableTracking, cancellationToken: ct)
            .ConfigureAwait(false)).ToDictionary(f => f.FileId);
        foreach (var file in tracked)
        {
            if (truth.TryGetValue(file.FileId, out var fromDb))
            {
                RestoreOwned(file, fromDb);
            }
        }

        // Change detection scans every tracked entity: run it once for the batch, then compare each file.
        _ = _unitOfWork.GetChanges();
        foreach (var file in tracked)
        {
            if (truth.TryGetValue(file.FileId, out var fromDb))
            {
                EnsureScalarsMatch(file, fromDb);
            }
        }

        return tracked;
    }

    // A reference navigation that was edited on the tracked instance becomes a foreign-key change when EF detects changes at
    // save time, even though the scalar was restored above. Force the detection now and compare: if a scalar moved away from
    // the database value again, refuse (409) instead of judging or persisting it. Collection navigations cannot be seen
    // through IUnitOfWork; see the README's accepted limits.
    private void EnsureNoPendingNavigationChange(TFile tracked, TFile truth)
    {
        _ = _unitOfWork.GetChanges();
        EnsureScalarsMatch(tracked, truth);
    }

    private static void EnsureScalarsMatch(TFile tracked, TFile truth)
    {
        foreach (var property in ScalarProperties)
        {
            if (!Equals(property.GetValue(tracked), property.GetValue(truth)))
            {
                throw new ApiException(
                    $"The tracked file {truth.FileId} has pending changes that the service refuses to judge or save (a navigation property was edited).",
                    null, LogLevel.Warning, null, HttpStatusCode.Conflict, "TRACKED_STATE_CONFLICT");
            }
        }
    }

    private static readonly System.Reflection.PropertyInfo[] ScalarProperties = typeof(TFile)
        .GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)
        .Where(p => p is { CanRead: true, CanWrite: true } && p.GetIndexParameters().Length == 0 && IsScalar(p.PropertyType))
        .ToArray();

    private static bool IsScalar(Type type)
    {
        var underlying = Nullable.GetUnderlyingType(type) ?? type;
        return underlying.IsValueType || underlying == typeof(string);
    }

    // Every mapped scalar of TFile (storage-owned, audit, allow-listed and host columns such as TenantId) is put back to
    // the database value, and ExtraProperties is copied from it. Only then is the allow-list applied on top.
    private static void RestoreOwned(TFile target, TFile truth)
    {
        foreach (var property in ScalarProperties)
        {
            property.SetValue(target, property.GetValue(truth));
        }

        target.ExtraProperties = truth.ExtraProperties is null
            ? new Dictionary<string, object>()
            : new Dictionary<string, object>(truth.ExtraProperties);
    }

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

    // Validates the client's file name, extension and declared size before anything is stored.
    private PreparedUpload Prepare(FileUpload content)
    {
        var fileName = SanitizeFileName(content.FileName);
        var extension = ValidateExtension(fileName);
        if (_options.MaxFileSizeBytes is { } max && content.Length is { } declared && declared > max)
        {
            throw CountingStream.TooLarge(max);
        }

        return new PreparedUpload(fileName, extension);
    }

    // Stores the original (and thumbnails for images) and records every object it wrote.
    private async Task<StoredContent> StoreContentAsync(FileType type, FileUpload content, PreparedUpload prepared,
        List<StoredObject> stored, CancellationToken ct)
    {
        var container = type.ToString();
        var path = $"{_timeProvider.GetUtcNow().ToString("yyyyMMdd", CultureInfo.InvariantCulture)}/{Guid.NewGuid():N}{prepared.Extension}";
        var writeOptions = new StorageWriteOptions { ContentType = ContentTypeMap.FromExtension(prepared.Extension) };

        await using var source = content.OpenReadStream();
        await using var counting = new CountingStream(source, _options.MaxFileSizeBytes);
        long size;
        if (type == FileType.Image)
        {
            // Thumbnails need a seekable copy; build them first so a bad image is rejected before anything is stored.
            using var buffer = new MemoryStream();
            await counting.CopyToAsync(buffer, ct).ConfigureAwait(false);
            size = buffer.Length;
            var bytes = buffer.GetBuffer();
            var length = (int)buffer.Length;
            var thumbnails = ThumbnailSizes.ToDictionary(s => s, s => ResizeToBytes(new MemoryStream(bytes, 0, length, false), s, path));

            using var original = new MemoryStream(bytes, 0, length, false);
            await _storageProvider.StoreAsync(original, path, writeOptions, container, ct).ConfigureAwait(false);
            stored.Add(new StoredObject(container, path));
            await StoreThumbnailsAsync(thumbnails, path, container, stored, ct).ConfigureAwait(false);
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

        return new StoredContent(path, size, prepared.Extension, prepared.FileName, mediaType);
    }

    private async Task StoreThumbnailsAsync(Dictionary<ImageSize, byte[]> thumbnails, string filePath, string container,
        List<StoredObject> stored, CancellationToken ct)
    {
        foreach (var (size, data) in thumbnails)
        {
            var thumbPath = ThumbnailPath(size, filePath);
            using var stream = new MemoryStream(data, writable: false);
            await _storageProvider.StoreAsync(stream, thumbPath,
                new StorageWriteOptions { ContentType = ContentTypeMap.FromExtension(Path.GetExtension(thumbPath)) },
                container, ct).ConfigureAwait(false);
            stored.Add(new StoredObject(container, thumbPath));
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
        var clean = ContentDispositionFormatter.StripInvisible(unified).Trim();
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
