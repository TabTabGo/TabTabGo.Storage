# TabTabGo.Storage

The domain package: ports, the `File` entity, path safety and an in-memory provider. It references `TabTabGo.Core`
only and has no ASP.NET Core dependency.

## `IStorageProvider`

| Member | Behaviour |
|---|---|
| `StoreAsync(byte[] buffer, extension, container)` | stores under a generated name, returns the relative path |
| `StoreAsync(path, buffer, container)` | stores at a path, overwriting |
| `StoreAsync(Stream, path, StorageWriteOptions?, container, ct)` | streamed upload, sets the content type, `Overwrite` defaults to `true` |
| `RetrieveAsync(path, container[, ct])` | whole object as a seekable `MemoryStream` at position 0; `null` when missing |
| `OpenReadAsync(path, container, ct)` | provider stream (may be forward-only); the caller disposes it |
| `ExistsAsync`, `DeleteAsync(path, container[, ct])` | delete is idempotent |
| `GetPublicUrlAsync(path, PublicUrlOptions, container, ct)` | time-limited URL (presigned / SAS), `null` when the backend has none |
| `GetPublicPath(...)` | legacy, `[Obsolete]` |

The members added after 6.x are default interface methods, so third-party implementations keep compiling. Derive
from `StorageProvider` to inherit the delegating defaults.

## `StoragePath`

```csharp
StoragePath.Normalize("a\\b//c.txt");      // "a/b/c.txt"
StoragePath.Combine("ThumbLarge", "2026"); // "ThumbLarge/2026"
StoragePath.IsSafe("../x");                // false
```

## `FileUpload`

`FileUpload.FromBytes`, `FromStream` (seekable streams can be re-read; a non-seekable stream opens once) and
`FromFactory` (opens lazily). `IFormFile.ToFileUpload()` lives in `TabTabGo.Storage.Services`.

## `InMemoryStorageProvider`

```csharp
var storage = new InMemoryStorageProvider();
var path = await storage.StoreAsync(bytes, ".png", "Image");
Assert.True(await storage.ExistsAsync(path, "Image"));
```

It is the reference implementation of the provider contract and is safe to use from parallel tests.
