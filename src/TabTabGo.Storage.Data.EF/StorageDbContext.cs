using Microsoft.EntityFrameworkCore;

namespace TabTabGo.Storage.Data.EF;

public class StorageDbContext : DbContext
{
    public StorageDbContext(DbContextOptions options) : base(options)
    {
    }

    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);
    }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);
    }
}