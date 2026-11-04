# TabTabGo.Storage.Services

`FileService<TFile>`, `ImageService` and the dependency-injection setup.

```csharp
services.AddTabTabGoStorageServices<File>(
    o => o.MaxFileSizeBytes = 20_000_000,        // StorageServiceOptions
    i => i.JpegQuality = 85);                    // ImageServiceOptions
```

The host also registers an `IStorageProvider` (one of the provider packages) and an `IUnitOfWork`.

## `FileService<TFile>` (`TFile : class, IFile`)

| Operation | Notes |
|---|---|
| `Upload(metadata, FileUpload)` | validates, stores the original (and thumbnails for images), inserts the row. If the insert fails the stored objects are removed. The client cannot choose the storage fields. |
| `Replace(id, FileUpload)` | stores new content, switches the row, removes the old objects from the container of the stored type |
| `Update(id, TFile)` / `Update(id, JsonObject)` / JSON patch | allow-list only: `Title`, `Comment`, `ReferenceType`, `ReferenceId`, `Duration`, `FileType`, `ExtraProperties` (merged). Changing `FileType` moves the object to the new type's container. |
| `Delete(id)`, `Delete(ids)` | **soft delete** (`IsEnabled = false`), objects kept, as in 6.x and Core. A soft-deleted file is 404 for every read and mutation by id |
| `Delete(predicate)` | hard-deletes the matching rows (as in 6.x), then removes their objects and thumbnails |
| `Purge(id)`, `Purge(ids)` | explicit hard delete of rows (soft-deleted ones too) and then their objects and thumbnails; on the class, not on `IFileService` |
| `GetFile(id)`, `GetImage(id, size)` | content type is derived from the stored extension; a missing thumbnail is generated and stored |
| `ListAsync(FileListQuery, scope)` | newest first, page size at most 100, `scope` is your tenant/owner filter |
| `Create(entity)` | refused: 400 `FILE_CONTENT_REQUIRED` |

Updates judge the **merged entity**: the row loaded by id with only the allow-listed client changes applied, so a
client-claimed `CreatedBy` or tenant can never satisfy an authorization hook. Storage-owned and audit fields are re-read
from the database before every mutation, so nothing left on a tracked instance can be persisted or used to address
another file's objects. `FileType` must be a defined value; `Unknown` on an update means "unchanged".

`IsValidToCreate` / `PreCreate` run before the content is stored (a veto stores nothing), so the storage fields are not
populated yet; use `PostCreate` for logic that needs them. Audit fields come from the metadata you pass: build it from the
authenticated user in server code and never bind it from a request body (`IsEnabled`, dates and `IsCompressed` are reset).

Every mutating overload runs the same hooks (`IsValidToCreate/Update/Delete`, `Pre*`, `Post*`); bulk deletes reject the
whole operation when one file fails validation.

### Upload rules (`StorageServiceOptions`)

`DeniedExtensions` (HTML, SVG, XML/XHTML/XSL, JS, executables by default), optional `AllowedExtensions`, `MaxFileSizeBytes` (default 50 MiB, 413; `null` = no limit), extension at most 10 characters including the dot (400). File names are reduced to a plain
name. The date folder uses UTC.

### Images (`ImageServiceOptions`, section `ImageService`)

`MaxPixels` (default 25 000 000, checked from the header before decoding) and `JpegQuality` (default 90). PNG, JPEG
and WEBP thumbnails keep their format; GIF, BMP and ICO sources produce PNG thumbnails. EXIF orientation is applied.
Thumbnail paths are `ThumbLarge|ThumbMedium|ThumbSmall/<original path>`.

## `IFormFile.ToFileUpload()`

```csharp
await fileService.Upload(new File { FileType = FileType.PDF, CreatedBy = user }, formFile.ToFileUpload());
```

## Known limits

- A thumbnail is decoded from the original once per size (four decodes per image upload); the pixel budget bounds each.
- Concurrent `GetImage` calls for a missing thumbnail may generate and store it more than once (same content, last write wins).
- 6.x wrote thumbnail keys with the OS separator (`ThumbLarge\\2026...`) on Windows; those raw backslash keys cannot be
  addressed through `StoragePath` and are not removed by `Delete(query)` / `Purge`.
