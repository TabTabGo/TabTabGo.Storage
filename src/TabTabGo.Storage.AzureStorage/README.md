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
- `LowercaseBlobNames` (default `true`) keeps compatibility with existing lower-case blobs; `A.png` and `a.png`
  collide while it is on. Turn it off for new, case-preserving deployments.
- Overwrite is the default; `StorageWriteOptions.Overwrite = false` uses an `If-None-Match: *` condition.
- Dependency note: `Azure.Core` 1.60 requires `Microsoft.Extensions.*` 10.0.x, so this package references those at 10.0.9.
- Tests: mock `BlobServiceClient`/`BlobClient` (they are mockable), or run Azurite through Testcontainers.
