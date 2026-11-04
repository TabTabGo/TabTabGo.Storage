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
| `Delete(id)`, `Delete(ids)` | **soft delete** (`IsEnabled = false`), objects kept, as in 6.x and Core. "Delete" means "hide": a soft-deleted file is 404 for every read and mutation by id and is also excluded from the inherited Core reads (`GetByKey`, `Exists`, `GetList`, `GetPageList`, OData, `GetViewModels`) |
| `Delete(predicate)` | hard-deletes the matching rows (as in 6.x), then removes their objects and thumbnails |
| `Purge(id)`, `Purge(ids)` | explicit hard delete of rows (soft-deleted ones too) and then their objects and thumbnails; on the class, not on `IFileService`. Gated by `IsValidToPurge` (defaults to `IsValidToDelete`) and by `PreDelete(file)` / `PostDelete(file)` |
| `GetFile(id)`, `GetImage(id, size)` | content type is derived from the stored extension; a missing thumbnail is generated and stored |
| `ListAsync(FileListQuery, scope)` | newest first, page size at most 100, `scope` is your tenant/owner filter |
| `Create(entity)` | refused: 400 `FILE_CONTENT_REQUIRED` |

The update gate holds in **both states**: `IsValidToUpdate(current, merged, out message)` (override it, or just
`IsValidToUpdate(entity)`, which the default calls for each) must accept the stored row *and* the stored row with the
allow-listed client changes applied. A client-claimed owner, tenant or reference therefore cannot satisfy an
authorization hook, and a record cannot be moved out of or into a scope the caller may not touch. Before every mutation
every mapped scalar property of `TFile` (storage-owned, audit, allow-listed and host columns such as a tenant id) and
`ExtraProperties` are re-read from the database and put back on the tracked instance, so nothing left on it can be
persisted, judged or used to address another file's objects. `FileType` must be a defined value; `Unknown` on an update means "unchanged".

`IsValidToCreate` / `PreCreate` run before the content is stored (a veto stores nothing). They see the real extension, name
and size of the content (the metadata's claims are overwritten), an empty `FilePath`, and no `ExtraProperties` unless
`StorageServiceOptions.AllowMetadataExtraProperties` is `true` (default `false`: a request body bound to the entity
puts every unknown property there). Use `PostCreate` for logic that needs the final path. Audit fields come from the metadata you pass: build it from the
authenticated user in server code and never bind it from a request body (`IsEnabled`, dates and `IsCompressed` are reset; `IsCompressed` becomes `false`, we never compress).

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
