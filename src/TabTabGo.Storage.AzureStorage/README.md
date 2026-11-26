# TabTabGo.Storage.Azure

`IStorageProvider` on Azure Blob Storage (`Azure.Storage.Blobs` 12.30). A container maps to a blob container; the name
is normalised to Azure's rules (3-63 characters, `[a-z0-9-]`, no `--`; shorter names are padded with `0`).

```csharp
// Managed identity: no secret anywhere. The host chooses the credential.
services.AddTabTabGoAzureBlobStorage(new Uri("https://account.blob.core.windows.net"), new DefaultAzureCredential());

// Configuration section AzureBlobStorage: ConnectionString | ServiceUri (+ code-supplied Credential),
// DefaultSasExpiry, LowercaseBlobNames
services.AddTabTabGoAzureBlobStorage(configuration.GetSection("AzureBlobStorage"));

// Legacy: reads ConnectionStrings:AzureStorageConnection
services.AddTabTabGoAzureBlobStorage();
```

Options are validated at startup: a connection string, or `ServiceUri` together with a `Credential`.

- One `BlobServiceClient` per process. Containers are created **only on write**, once per container per process.
- `GetPublicUrlAsync` returns an absolute SAS URL (never logged or printed). With an account key it is a service
  SAS; with a token credential it is a **user-delegation** SAS. If neither is possible it returns `null`.
  Content type and disposition overrides follow the same rule as S3: the type of the object's own extension, and
  `attachment` unless the type is safe to render and `Inline` was requested.
- `LowercaseBlobNames` (default `true`) keeps compatibility with existing lower-case blobs, so **`A.png` and `a.png`
  are the same blob** (S3 and the file system are case-sensitive). `FileService` names are GUID paths and cannot collide;
  callers that choose their own paths must not rely on case. Turn it off for new, case-preserving deployments.
- Container names: unsafe input (`../x`, `a/b`, control characters) is a 400; a safe name maps exactly as 6.x did
  (`my_files` -> `myfiles`); only names Azure used to reject (`a--b`, fewer than 3 characters) are adjusted.
- Overwrite is the default; `StorageWriteOptions.Overwrite = false` uses an `If-None-Match: *` condition.
- Dependency note: this line uses `Azure.Storage.Blobs` 12.30.1. The v8 line pins 12.27.0 because 12.28+ forces
  `Microsoft.Extensions.*` and `System.Text.Json` 10.x onto net8.0.
- A SAS URL is served by Azure without our headers (no `nosniff`), so the response type is always the object's own
  extension type and `text/plain` is never inline.
- Tests: mock `BlobServiceClient`/`BlobClient` (they are mockable), or run Azurite through Testcontainers.

A blob name (the path) is at most 1024 characters; a longer path is a 400 `STORAGE_INVALID_PATH`.
