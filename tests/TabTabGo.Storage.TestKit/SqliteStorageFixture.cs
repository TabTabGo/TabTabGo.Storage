using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TabTabGo.Data.EF;
using TabTabGo.Storage.Data.EF;

namespace TabTabGo.Storage.TestKit;

/// <summary>A context that maps <see cref="Entities.File"/> through <see cref="StorageModelsBuilder"/>.</summary>
public sealed class TestDbContext(DbContextOptions<TestDbContext> options) : DbContext(options)
{
    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder) => StorageModelsBuilder.OnModelCreating(modelBuilder);
}

/// <summary>Unit of work over a <see cref="TestDbContext"/>.</summary>
public sealed class TestUnitOfWork(DbContext context) : UnitOfWork(context, NullLogger<UnitOfWork>.Instance);

/// <summary>An in-memory SQLite database with the storage schema, created fresh per instance.</summary>
public sealed class SqliteStorageFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    /// <summary>Creates the database and schema.</summary>
    public SqliteStorageFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();
        Context = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options);
        Context.Database.EnsureCreated();
        UnitOfWork = new TestUnitOfWork(Context);
    }

    /// <summary>The EF context.</summary>
    public TestDbContext Context { get; }

    /// <summary>The unit of work.</summary>
    public TestUnitOfWork UnitOfWork { get; }

    /// <summary>Opens a second context on the same database (to check what was really persisted).</summary>
    public TestDbContext NewContext() =>
        new(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options);

    /// <inheritdoc />
    public void Dispose()
    {
        Context.Dispose();
        _connection.Dispose();
    }
}
