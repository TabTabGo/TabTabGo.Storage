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

- `RetrieveAsync` / `OpenReadAsync` return `null` only when the object does not exist (S3 also answers 403 for a missing key without `s3:ListBucket`, which is reported as not found with a warning log); `RetrieveAsync` returns a
  seekable stream at position 0.
- `StoreAsync` returns the stored relative path and never `null`; it **overwrites** unless
  `StorageWriteOptions.Overwrite = false`.
- `DeleteAsync` is idempotent (a missing object is a no-op).
- Any other failure throws `StorageException` (an `ApiException`: 400 for an invalid path, 409 for an existing object
  when overwrite is off, 502 for upstream failures). It carries provider, operation, container and path, never a
  credential or a signed URL.
- Every path goes through `StoragePath.Normalize`: `\` becomes `/`, duplicate separators collapse; rooted, UNC and
  drive paths, `.`/`..` segments, `:`, control characters and Unicode format characters (category Cf) are rejected, and over-long paths (file-system segment > 255 UTF-8 bytes, S3 key > 1024 UTF-8 bytes, Azure blob name > 1024 characters) are a 400 `STORAGE_INVALID_PATH`.

## Security notes

- **Store outside the web root.** Files served by static-file middleware execute in the browser. `PathService`
  logs a warning when the storage root is under `wwwroot`.
- **Denied extensions.** `StorageServiceOptions.DeniedExtensions` refuses HTML, SVG, XML/XHTML/XSL, JavaScript and
  executables by default; use `AllowedExtensions` for an allow-list. An extension is at most 10 characters.
- **Reads have no `IsValidTo*` hook.** `GetFile` / `GetImage` by id go through `GetKeyPredicate`; to scope reads per tenant or
  owner, override `GetKeyPredicate(long id)`. The default is `FileId == id && IsEnabled`: an override that drops the `IsEnabled`
  filter makes soft-deleted files readable again, so it must re-add it (`entity.FileId == id && entity.IsEnabled && <your scope>`).
- **The default deny-list is not exhaustive.** It does not list `.asp`, `.ashx`, `.asmx`, `.cshtml`, `.vbs`, `.wsf`, `.jar`,
  `.msi`, `.scr`, `.lnk`, `.mht`, `.xaml`, `.htc` or `.phtml`. They were left out on purpose: adding them would refuse uploads
  that 6.x and 8.0.0 accept, which is a breaking change, so the list can only grow in a major version. They are not
  browser-executable through this library, because content types come from the extension and script-capable types are never
  mapped to their native type (served as `application/octet-stream`). If your host executes or opens such files, set
  `DeniedExtensions` to a longer list or, better, use the `AllowedExtensions` allow-list.
- **Content type comes from the extension**, never from the client (`ContentTypeMap`). Script-capable types are
  never mapped to their native type, and signed URLs only override the response type to the type of the object's
  own extension (the caller's string is never echoed).
- **Image resize to a path is all-or-nothing.** `IImageService.Resize(..., string outputPath, ...)` encodes in memory and
  writes the file only on success: a corrupt or oversized image leaves no new file and never truncates an existing one.
- **Generated names validate the extension.** The `StoreAsync(byte[], extension, container)` overload of every provider
  (disk, S3, Azure) accepts `[A-Za-z0-9]` extensions only and throws `STORAGE_INVALID_PATH` otherwise.
- **Azure `Overwrite = false`** reports `STORAGE_OBJECT_EXISTS` only for `BlobAlreadyExists` / a failed `If-None-Match`;
  other 409s (container being deleted, lease conflicts) surface as the generic storage failure.
- **Updates cannot move files.** `FilePath`, `FileSize`, `FileExtension`, `OriginalFileName` and
  `OriginalMediaType` change only through `Upload` / `Replace`.
- **Signed URLs are served by the storage provider, without our headers** (no `X-Content-Type-Options: nosniff`). They
  therefore only ever carry the content type of the object's own extension, `text/plain` is never inline, and active
  content is always an attachment.
- **Credentials:** use an IAM role (S3) or managed identity (Azure). Presigned URLs and SAS live between 1 minute
  and 7 days; a SAS signed with a token credential (user-delegation key) is capped slightly below that, at 6 days 23 hours 54 minutes, because Azure limits the key to 7 days from its start and the start is placed 5 minutes back for clock skew.
- Thumbnail generation rejects images over `ImageServiceOptions.MaxPixels` (decompression-bomb guard).
- **S3 uploads of non-seekable streams are buffered in memory** (the SDK needs the length), so keep `StorageServiceOptions.MaxFileSizeBytes` (default 50 MiB) bounded: raising it or removing it raises per-upload memory with it.
- **`StoragePath` accepts segments with a trailing dot or space** (for example `a./b`). They alias to `a/b` on Windows with the file-system provider only; S3 and Azure keep them distinct. Do not rely on such names being distinct on Windows.

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
| `GetFile` / `GetImage` throw `ApiException` 404 `FILE_NOT_FOUND` (not `FileNotFoundException`) when the row or its object is missing, and `FileResult.ContentType` now comes from the **stored extension**, never from the media type the client sent (spec SB16, X-2) | catch `ApiException` and read `FileResult.ContentType` instead of the original upload's media type |
| A whole-entity `Update` keeps the stored value when the incoming `ReferenceId` / `Duration` (like `Title`, `Comment`, `ReferenceType`) is null (spec SB45); a consumer entity with a non-nullable `ReferenceId` always copies it | clear a value with a JSON patch (`Update(id, JsonObject)`) |
| The list filter converts `ReferenceType` / `ReferenceId` to the consumer entity's member type (string or enum, `long` or `long?`); an unknown enum name is 400 `FILTER_NOT_SUPPORTED` instead of a 500 (spec SB44) | none; query with a defined enum name |
| The obsolete `GetPublicPath(..., expiryDays)` **ignores `expiryDays`** on S3 and Azure and uses the provider default lifetime (S3 `ExpirationMinutes`, default 60; Azure `DefaultSasExpiry`, default 1 hour) (spec SB25) | call `GetPublicUrlAsync` with `PublicUrlOptions.ExpiresIn` when the lifetime matters |
| **S3 403 on a read is "not found"** (owner decision, BL-2): S3 answers 403 for a missing key when the caller lacks `s3:ListBucket`, so `RetrieveAsync` and `OpenReadAsync` return `null` on a 403 whose error code is `AccessDenied`, and `ExistsAsync` returns `false` on a 403 HEAD. `AccessDenied` also covers a KMS decrypt denial, a missing `s3:GetObject`, an explicit bucket or VPC-endpoint policy `Deny` and a service control policy: **all of them are reported as not found**, so alert on the warning below. A HEAD answer has no body and never carries a specific code (the SDK reports `ErrorCode` `Forbidden`), so on `ExistsAsync` a credential, signature, clock or account fault (`InvalidAccessKeyId`, `SignatureDoesNotMatch`, `RequestTimeTooSkewed`, `AccountProblem`, `AllAccessDisabled`) also returns `false`, logged with the code `Forbidden`; it surfaces on the next read or write. On `RetrieveAsync` / `OpenReadAsync` those codes and `InvalidObjectState` still throw, as do writes, deletes and signing. The mapping logs one warning with the fixed event id 4403 `S3ReadForbiddenAsNotFound` carrying the provider, operation, container, file extension, a 12-hex-character SHA-256 prefix of the normalised path, the S3 error code, the request id and the status (never the raw path, key, bucket, credentials or URLs). The hash cannot be reversed for random names (`FileService` paths are GUIDs), but a guessable caller-chosen name can be confirmed by hashing candidates. The warning needs the injected logger (the unsupported parameterless constructor throws) | grant the role `s3:ListBucket` on the bucket to tell a missing key from a permission fault. Ops: alert on event 4403 `S3ReadForbiddenAsNotFound`; without `s3:ListBucket` every legitimate miss logs it too, so expect noise until the permission is granted |
| **Length limits on paths** (400 `STORAGE_INVALID_PATH`, not a 502): a file-system path segment is at most 255 UTF-8 bytes; an S3 object key is at most 1024 UTF-8 bytes including `<container>/` (a container of 1023 bytes or more is a 400 `STORAGE_INVALID_CONTAINER`); an Azure blob name is at most 1024 characters | keep caller-chosen paths short; `FileService` GUID paths are far below the limits |
| **Unicode format characters (category Cf, for example U+202E right-to-left override, U+200B zero-width space, U+00AD soft hyphen) are rejected** by `StoragePath` on every provider and in `InMemoryStorageProvider` (400 `STORAGE_INVALID_PATH`). **Objects already stored with such a character in their name can no longer be read, checked or deleted through the provider (400)** | strip them from caller-supplied names; read and rename existing objects with the native SDK (S3 / Azure / file system) before upgrading callers |
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
