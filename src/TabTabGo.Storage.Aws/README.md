# TabTabGo.Storage.Aws

`IStorageProvider` on AWS S3 (AWS SDK for .NET **v4**). A container is a key prefix inside one bucket.

```csharp
services.AddTabTabGoS3Storage(configuration.GetSection("aws"));
```

| Setting (`aws:`) | Meaning |
|---|---|
| `BucketName` | required |
| `Region` | required unless `ServiceUrl` is set |
| `AccessKey`, `SecretKey` | optional. **Leave them empty**: the SDK default credential chain (IAM role, environment, profile) is used. Never logged. |
| `ExpirationMinutes` | default lifetime of presigned URLs (60) |
| `ServiceUrl`, `ForcePathStyle` | S3-compatible services and emulators (LocalStack, MinIO) |
| `TimeoutSeconds` | request timeout (100) |

One `IAmazonS3` client is created per process and shared (register your own `IAmazonS3` first to override it).
Every `AWSSDK.*` package in your application must be on 4.x (one `AWSSDK.Core` major per app).

- Objects are stored with a content type derived from the extension.
- `GetPublicUrlAsync` returns a presigned GET URL (1 minute to 7 days). A `ContentType` / `DownloadFileName`
  override becomes a response header override; the content type is always the one of the object's own extension and
  the disposition is `attachment` unless the type is safe to render and `Inline` was requested.
- A forward-only stream for large objects: `OpenReadAsync`.
- Tests: `S3StorageProvider` takes an `IAmazonS3`, so mock it, or run LocalStack through Testcontainers.
