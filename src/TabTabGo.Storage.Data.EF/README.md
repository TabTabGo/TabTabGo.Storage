# TabTabGo.Storage.Data.EF

Entity Framework Core mapping for `TabTabGo.Storage.Entities.File`. **This package ships no migrations.**

```csharp
protected override void OnModelCreating(ModelBuilder modelBuilder)
    => StorageModelsBuilder.OnModelCreating(modelBuilder);
```

The mapping is identical to 6.0.8.4: table `Files`, the same columns, types, lengths and nullability. A golden-model
test enforces it, so adopting this version needs no schema change. The audit columns `CreatedBy` / `UpdatedBy` are
pinned as required even when a newer Core makes them nullable.

- The old `OnModelCreating(modelBuilder, schema)` overloads **ignore** the `schema` argument, as they always did
  (applying it would move the table).
- `StorageModelOptions` is the explicit way to change the mapping: `Schema` (default none), `TableName`
  (`Files`), `CommentColumnType` (`NTEXT`, SQL Server only; set `null` for other databases). Changing a default
  changes your schema and is your own migration.
- `StorageDbContext` maps `File` and `StorageDbProvider` is a ready unit of work:

```csharp
services.AddDbContext<StorageDbContext>(o => o.UseSqlServer(connectionString));
services.AddScoped<IUnitOfWork, StorageDbProvider>();
```

If you map your own `IFile` entity, write your own builder (as Techio does) and register your own unit of work.
