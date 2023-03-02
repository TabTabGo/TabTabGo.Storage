# TabTabGo Storage
A list of library help to manage files and file storage 
## TabTabGo Storage
a core library for TabTabGo Storage include entity, repository interface, service interface, and other helper
## TabTabGo Storage Service
a service library for TabTabGo Storage include service implementation
## TabTabGo Storage Data Entity Framework
a data entity framework library for TabTabGo Storage include entity framework model builder and repository implementation
## Supported Storage Providers
based on provider can define location to store a file (local, azure blob storage, aws s3, etc)
to add new provider, just implement IStorageProvider interface
to use provider, just inject IStorageProvider interface to your service
### Local Storage Provider 
a local storage provider library for TabTabGo Storage include local storage provider implementation
in program.cs, add this line
```csharp
services.Register<IStorageProvider,FileStorageProvider>();
```
### Azure Blob Storage Provider
a azure blob storage provider library for TabTabGo Storage include azure blob storage provider implementation
in program.cs, add this line
```csharp
services.Register<IStorageProvider,BlobStorageProvider>();
```
### AWS S3 Storage Provider
a aws s3 storage provider library for TabTabGo Storage include aws s3 storage provider implementation
in program.cs, add this line
```csharp
services.Register<IStorageProvider,S3StorageProvider>();
```
