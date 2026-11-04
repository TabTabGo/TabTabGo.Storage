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
| `Delete(id)`, `Delete(ids)`, `Delete(predicate)` | hard-delete the rows, then remove the objects and thumbnails (failures are logged) |
| `GetFile(id)`, `GetImage(id, size)` | content type is derived from the stored extension; a missing thumbnail is generated and stored |
| `ListAsync(FileListQuery, scope)` | newest first, page size at most 100, `scope` is your tenant/owner filter |
| `Create(entity)` | refused: 400 `FILE_CONTENT_REQUIRED` |

Every mutating overload runs the same hooks (`IsValidToCreate/Update/Delete`, `Pre*`, `Post*`); bulk deletes reject the
whole operation when one file fails validation.

### Upload rules (`StorageServiceOptions`)

`DeniedExtensions` (HTML, SVG, XML/XHTML/XSL, JS, executables by default), optional `AllowedExtensions`, optional
`MaxFileSizeBytes` (413), extension at most 10 characters including the dot (400). File names are reduced to a plain
name. The date folder uses UTC.

### Images (`ImageServiceOptions`, section `ImageService`)

`MaxPixels` (default 50 000 000, checked from the header before decoding) and `JpegQuality` (default 90). PNG, JPEG
and WEBP thumbnails keep their format; GIF, BMP and ICO sources produce PNG thumbnails. EXIF orientation is applied.
Thumbnail paths are `ThumbLarge|ThumbMedium|ThumbSmall/<original path>`.

## `IFormFile.ToFileUpload()`

```csharp
await fileService.Upload(new File { FileType = FileType.PDF, CreatedBy = user }, formFile.ToFileUpload());
```
