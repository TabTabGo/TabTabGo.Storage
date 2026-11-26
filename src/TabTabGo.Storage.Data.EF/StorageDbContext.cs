using Microsoft.EntityFrameworkCore;

namespace TabTabGo.Storage.Data.EF;

/// <summary>A ready-made context that maps <see cref="Entities.File"/> through <see cref="StorageModelsBuilder"/>.</summary>
public class StorageDbContext : DbContext
{
    /// <summary>Creates the context.</summary>
    /// <param name="options">Context options.</param>
    public StorageDbContext(DbContextOptions options) : base(options)
    {
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
        StorageModelsBuilder.OnModelCreating(modelBuilder);
    }
}
