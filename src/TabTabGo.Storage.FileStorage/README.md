# TabTabGo.Storage.FileStorage

`IStorageProvider` on the local file system.

```csharp
services.AddTabTabGoFileStorage(configuration.GetSection("PathSettings"));
```

```json
{
  "PathSettings": {
    "RootPath": "/var/lib/myapp/files",
    "DateFormat": "yyyyMMdd",
    "PublicBaseUrl": "https://files.example.com",
    "FileTypePaths": { "Image": "images", "PDF": "pdf" }
  }
}
```

- `RootPath`: relative values resolve against the content root (`IHostEnvironment.ContentRootPath`); absolute values
  are kept. **Keep it outside the web root**; a warning is logged otherwise.
- `FileTypePaths`: container (file type) to folder under `RootPath`, case-insensitive. An unmapped container uses a
  folder named after the container. Backslashes in configuration written on Windows are treated as separators.
- `DateFormat`: format of the UTC date folder for generated names (default `yyyyMMdd`).
- `PublicBaseUrl`: the URL that serves `RootPath` if your host exposes it. `GetPublicUrlAsync` returns
  `PublicBaseUrl/<folder>/<path>`, or `null` when it is not set.

Behaviour: every path is normalised and must stay under the container root (traversal and rooted paths throw a 400
`StorageException`); nested folders are created; writes go to a temporary file in the same folder and are moved into
place (readers never see a partial file); generated names are unique under concurrency; `OpenReadAsync` returns a
`FileStream`; deleting a missing file is a no-op. The provider is a thread-safe singleton and uses `TimeProvider` (UTC).
