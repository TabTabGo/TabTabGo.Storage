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
- `StoreAsync` with `Overwrite = false` is a single conditional PUT (`If-None-Match: *`), so concurrent writers cannot both win.
- A presigned URL is served by S3 without our headers (no `X-Content-Type-Options: nosniff`): the response type is always
  the type of the object's own extension and `text/plain` is never inline.
- A forward-only stream for large objects: `OpenReadAsync`.
- A 403 on a read is reported as not found (owner decision, BL-2), because S3 answers 403 for a missing key when the caller
  lacks `s3:ListBucket`: `RetrieveAsync` / `OpenReadAsync` return `null` when the error code is `AccessDenied` (a KMS decrypt
  denial is `AccessDenied` too and is mapped as well), and `ExistsAsync` returns `false` on a 403 HEAD (no body: the SDK
  reports the code `Forbidden`). `InvalidAccessKeyId`, `SignatureDoesNotMatch`, `RequestTimeTooSkewed`, `AccountProblem`,
  `AllAccessDisabled` and `InvalidObjectState` still throw, as do writes, deletes and signing.
- The mapping logs a warning with the event id `S3ReadForbiddenAsNotFound`: provider, operation, container, file extension,
  a 12-hex-character SHA-256 prefix of the normalised path, the S3 error code, the request id and the status. Never the path,
  key, bucket, credentials or URLs. **Ops:** alert on the event id; without `s3:ListBucket` every legitimate miss also logs it.
  The warning goes through the injected logger (the unsupported parameterless constructor throws).
- An object key (`<container>/<path>`) is at most 1024 UTF-8 bytes; a longer path is a 400 `STORAGE_INVALID_PATH`.
- Tests: `S3StorageProvider` takes an `IAmazonS3`, so mock it, or run LocalStack through Testcontainers.
