# TabTabGo Storage

File storage for .NET: one port (`IStorageProvider`), three providers (AWS S3, Azure Blob, local disk), a `File`
entity and `FileService<TFile>` (upload, replace, update, read, delete, image thumbnails), and an EF Core model
builder. This is the **v8 line** (`net8.0`, package version `8.x`, depends on `TabTabGo.Core` 8.0.1 or later 8.x).

## Packages

| Package | What it is |
|---|---|
| `TabTabGo.Storage` | Domain and ports: `IStorageProvider`, `File`/`IFile`, `StoragePath`, `FileUpload`, `StorageException`, `ContentTypeMap`, `InMemoryStorageProvider` for tests. No ASP.NET Core dependency. |
| `TabTabGo.Storage.Services` | `FileService<TFile>`, `ImageService` (SkiaSharp), dependency-injection setup, `IFormFile.ToFileUpload()` |
| `TabTabGo.Storage.Data.EF` | `StorageModelsBuilder`, `StorageDbContext`, `StorageDbProvider`. Ships no migrations. |
| `TabTabGo.Storage.Aws` | S3 provider (AWS SDK for .NET v4) |
| `TabTabGo.Storage.Azure` | Azure Blob provider (connection string or managed identity) |
| `TabTabGo.Storage.FileStorage` | Local file-system provider |

Release lines: **v8** (this one, .NET 8 - note .NET 8 support ends 2026-11-10, so v8 is a bridge line), **v10**
(.NET 10 LTS, the long-term home) and **v11** (.NET 11 pre-releases). Fixes land on v8 first and are merged forward.

## Choosing a provider

| Need | Provider |
|---|---|
| Containers/Kubernetes, many instances, durable object storage on AWS | `.Aws` |
| Azure hosting, managed identity | `.Azure` |
| Single host, development, on-premises | `.FileStorage` (keep the root outside the web root) |
| Tests of your own code | `InMemoryStorageProvider` (`TabTabGo.Storage.Testing`) |

## Quick start

Register exactly one provider; credentials do not belong in configuration.

```csharp
// S3: leave AccessKey/SecretKey empty to use the IAM role / default AWS credential chain.
services.AddTabTabGoS3Storage(configuration.GetSection("aws"));        // aws:BucketName, aws:Region

// Azure: a token credential chosen by the host (managed identity), no account key.
services.AddTabTabGoAzureBlobStorage(new Uri("https://account.blob.core.windows.net"), new DefaultAzureCredential());

// Local disk
services.AddTabTabGoFileStorage(configuration.GetSection("PathSettings")); // PathSettings:RootPath, FileTypePaths:Image ...

// File service over your unit of work (for example StorageDbProvider from .Data.EF)
services.AddTabTabGoStorageServices<TabTabGo.Storage.Entities.File>(o => o.MaxFileSizeBytes = 20_000_000);
```

Upload:

```csharp
var file = await fileService.Upload(new File { Title = "Invoice", FileType = FileType.PDF, CreatedBy = user },
                                    formFile.ToFileUpload(), cancellationToken);
```

## Provider contract

Every provider passes the same contract suite:

- `RetrieveAsync` / `OpenReadAsync` return `null` only when the object does not exist; `RetrieveAsync` returns a
  seekable stream at position 0.
- `StoreAsync` returns the stored relative path and never `null`; it **overwrites** unless
  `StorageWriteOptions.Overwrite = false`.
- `DeleteAsync` is idempotent (a missing object is a no-op).
- Any other failure throws `StorageException` (an `ApiException`: 400 for an invalid path, 409 for an existing object
  when overwrite is off, 502 for upstream failures). It carries provider, operation, container and path, never a
  credential or a signed URL.
- Every path goes through `StoragePath.Normalize`: `\` becomes `/`, duplicate separators collapse; rooted, UNC and
  drive paths, `.`/`..` segments, `:` and control characters are rejected.

## Security notes

- **Store outside the web root.** Files served by static-file middleware execute in the browser. `PathService`
  logs a warning when the storage root is under `wwwroot`.
- **Denied extensions.** `StorageServiceOptions.DeniedExtensions` refuses HTML, SVG, XML/XHTML/XSL, JavaScript and
  executables by default; use `AllowedExtensions` for an allow-list. An extension is at most 10 characters.
- **Content type comes from the extension**, never from the client (`ContentTypeMap`). Script-capable types are
  never mapped to their native type, and signed URLs only override the response type to the type of the object's
  own extension (the caller's string is never echoed).
- **Updates cannot move files.** `FilePath`, `FileSize`, `FileExtension`, `OriginalFileName` and
  `OriginalMediaType` change only through `Upload` / `Replace`.
- **Signed URLs are served by the storage provider, without our headers** (no `X-Content-Type-Options: nosniff`). They
  therefore only ever carry the content type of the object's own extension, `text/plain` is never inline, and active
  content is always an attachment.
- **Credentials:** use an IAM role (S3) or managed identity (Azure). Presigned URLs and SAS live between 1 minute
  and 7 days.
- Thumbnail generation rejects images over `ImageServiceOptions.MaxPixels` (decompression-bomb guard).

## Upgrading from 6.x

| Change | Migration |
|---|---|
| `net8.0` only; Core 8.0.1 | consume the v8 Core line |
| AWS SDK v3 to v4 | move every `AWSSDK.*` package of the app to 4.x |
| `IFile.FormFile` removed | `Upload(entity, formFile.ToFileUpload())`; `Create(entity)` now returns 400 `FILE_CONTENT_REQUIRED`. Add `<FrameworkReference Include="Microsoft.AspNetCore.App" />` to projects that used `IFormFile` through this package |
| Updates ignore storage-owned fields | use `Replace(id, upload)` to change content |
| `Store` throws instead of returning `null`; missing objects return `null` on every provider | catch `StorageException` |
| S3 `Retrieve` stream is at position 0 | a leftover `Seek(0)` is harmless |
| Azure `GetPublicPath` returns an absolute SAS URL | stop prefixing a host |
| LightInject composition roots removed | `AddTabTabGo...()` |
| `S3StorageProvider` / `BlobStorageProvider` constructors | resolve through DI; the old constructors are `[Obsolete]` |
| `PathService(IHostEnvironment, ...)` | DI is unaffected |
| `FileResult` fields are properties and `FileResult` is `IAsyncDisposable` | recompile; dispose it |
| `ImageSizeExtensions` is in `TabTabGo.Storage.Services` | add the `using` |
| `FileNotFoundException` / `TTGException` become `ApiException` (404/400) | update exception filters |
| `FileService.Update(long, JObject)` (Newtonsoft) became `Update(long, JsonObject)` (System.Text.Json), Core 8 API | switch the argument type |
| `IPathService.PathSettings { get; }` is a new abstract member | custom `IPathService` implementers must add it |
| `FileService.PostCreate` / `GetImage` signatures changed (`CancellationToken`, nullable, `FileResult` is `IAsyncDisposable`) | recompile overrides and dispose the result |
| `StorageDbContext.OnConfiguring` is no longer overridden | none unless you derived from it and relied on the override |
| A container is exactly one path segment on every provider (`a/b` and `../x` are 400) | use single-segment container names |
| Updates run `IsValidToUpdate` on the stored row merged with the allow-listed client changes; `UpdateChanges` routes through the same allow-list | review any `IsValidToUpdate` override that read client-only fields |
| `FileType` must be a defined value (400 otherwise); `Unknown` on an update means "unchanged" | none |
| Container names are lower-cased with the invariant culture (6.x used the current culture: under tr-TR `Image` became `mage`) | a 6.x host that ran under a Turkish or Azerbaijani culture may have data in `mage`; copy it to `image` |
| Soft-deleted files are hidden from the inherited Core reads too; `Purge` is gated by `IsValidToPurge` and `PreDelete(file)` | none |
| Bulk deletes (`Delete(ids)`, `Delete(query)`, `Purge(ids)`) now also call the single-entity `PreDelete(file)` / `PostDelete(file)` | hosts that count hook calls |
| `ExtraProperties` of an upload **and of an update** are ignored unless `AllowMetadataExtraProperties` is set (then bounded to 32 keys / 16 KiB, 400 above); `IsCompressed` is `false` | set the option and the limits if you rely on client-supplied extras (spec row SB32) |
| `UpdatedDate` is set by the service on update, replace and soft delete; `CanDelete` is `false` for a missing or soft-deleted file | set `UpdatedBy` in `PreUpdate` or in your interceptor |
| An edited reference navigation on a tracked file is refused with 409 `TRACKED_STATE_CONFLICT` | do not edit tracked entities before calling the service |
| `StorageServiceOptions.MaxFileSizeBytes` defaults to 50 MiB and `ImageServiceOptions.MaxPixels` to 25 000 000 | raise them explicitly if you accept larger files |
| GIF/BMP/ICO thumbnails are PNG; JPEG quality 90 | none |
| `Delete(id)` / `Delete(ids)` are still **soft deletes** (`IsEnabled = false`, objects kept); a soft-deleted file is now hidden from every read and mutation by id | none. New: `FileService<TFile>.Purge(id)` / `Purge(ids)` hard-delete the row and remove the objects (class only, not on `IFileService`); `Delete(query)` stays a hard delete as in 6.x |
| `StorageDbContext` maps `File` | only matters if you generated migrations from it |
| `TabTabGo.Storage.WebApi` (never published) | removed; a minimal-API read package is planned |

## Building, testing, releasing

```bash
dotnet restore
dotnet build -c Release -warnaserror
dotnet test -c Release
```

`global.json` pins the SDK. While `TabTabGo.Core` 8.0.1 is not on nuget.org, build against a local Core:

```bash
python3 scripts/local-core.py                       # packs Core v8 as 8.0.1-local.<sha> and writes nuget.local.config (git-ignored)
export TabTabGoCoreVersion=8.0.1-local.<sha>        # printed by the script; overrides the [8.0.1, 9.0.0) range
dotnet restore TabTabGo.Storage.sln --configfile nuget.local.config
dotnet build TabTabGo.Storage.sln -c Release -warnaserror --no-restore
```

The local packages live in `tabtabgo/.local-nuget` (never committed); the Core worktree is not touched.
**Unset the variable when you are done** (`unset TabTabGoCoreVersion`): a leftover `export` keeps every build on the
local Core even after 8.0.1 is published on nuget.org. Never publish
with the variable set: `release.yml` refuses it. Emulator tests (LocalStack 4.0, Azurite) need Docker and skip
themselves without it; set `TTG_SKIP_EMULATORS=true` to skip them explicitly.

API compatibility against the published 6.0.8.4 libraries is checked by `python3 scripts/apicompat.py` (every
suppression in `compat/` cites its spec 6.4 row); `python3 scripts/mutation-check.py` re-introduces the registered bugs
and proves a test kills each one; `Golden/capture/capture.py` re-captures the EF model golden files from the 6.0.8.4 sources.

**Releasing.** `.github/workflows/release.yml` publishes only from a tag `vMAJOR.MINOR.PATCH[-rc.N]` whose major
equals the target framework major, whose commit is on `releases/vMAJOR.MINOR.x/*`, whose version equals
`Directory.Build.props` `Version`, and (for a stable tag) whose Core dependency is stable. Stable releases wait for
manual approval in the `nuget-release` environment; the repository variable `RELEASE_APPROVAL_MODE=environment`
must be set or the workflow refuses to publish. Publishing uses nuget.org trusted publishing (`NUGET_USER`) with an
environment-scoped API key as the only fallback. CI (`ci.yml`) never publishes.

## Accepted limits

- The symlink check of the file-system provider resolves links and then uses the path (check-then-use): a link swapped in
  between the check and the open by someone who can write inside the storage root is not prevented. Keep the root writable
  only by the application.
- Two concurrent requests for the same missing thumbnail may both generate and store it (identical bytes, last write wins).
- Thumbnail keys written by 6.x with a backslash separator cannot be addressed through `StoragePath`, so `Delete(query)` and
  `Purge` do not remove them.
- Edited **collection** navigations of a host `TFile` (for example a many-to-many tag list) cannot be detected through
  `IUnitOfWork`; they are neither restored nor refused. Contexts are per request: do not let untrusted code edit tracked entities
  before calling the service.
- Unicode "format" (Cf) characters are stripped from names, including ZWJ inside emoji sequences, so such names lose the joiner.
  The joining characters that Persian and Indic scripts rely on (ZWNJ U+200C and ZWJ U+200D) are removed from file names too,
  so a name written with them is stored and served without them; the visible letters are kept.
- Do not share a `DbContext` that holds untrusted changes with the service: change detection cannot see edited collection
  navigations, so such an edit is neither restored nor refused. Use a fresh context per request.
- Azure containers must never be derived from user input: a container name is configuration, not a request field.
- A 6.x host that ran under a Turkish or Azerbaijani culture may hold data in a container named `mage` (6.x lower-cased
  `Image` with the dotless i); v8 lower-cases invariantly, so that data is not found under `image` and has to be copied.

## License

MIT.
