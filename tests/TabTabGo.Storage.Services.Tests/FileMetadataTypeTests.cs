using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TabTabGo.Core.Entities;
using TabTabGo.Core.Exceptions;
using TabTabGo.Data.EF;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.TestKit;
using TabTabGo.Storage.Testing;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

public enum StrictReferenceKind
{
    None = 0,
    Order = 1,
}

// A consumer entity whose reference members are not the string / long? the library's own File uses.
public class StrictFile : Entity, IFile
{
    public long FileId { get; set; }

    public string Title { get; set; } = string.Empty;

    public string FileExtension { get; set; } = string.Empty;

    public FileType FileType { get; set; }

    public string OriginalFileName { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;

    public long? FileSize { get; set; }

    public string Comment { get; set; } = string.Empty;

    public string OriginalMediaType { get; set; } = string.Empty;

    public bool? IsCompressed { get; set; }

    public StrictReferenceKind ReferenceType { get; set; }

    public long ReferenceId { get; set; }
}

public class FileListFilterTypeTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("DataSource=:memory:");
    private readonly StrictContext _context;

    public FileListFilterTypeTests()
    {
        _connection.Open();
        _context = new StrictContext(new DbContextOptionsBuilder<StrictContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private FileService<StrictFile> Service() =>
        new(new TestUnitOfWork(_context), NullLogger<FileService<StrictFile>>.Instance, new InMemoryStorageProvider(), new ImageService());

    private static async Task SeedAsync(FileService<StrictFile> service) =>
        await service.Upload(
            new StrictFile { Title = "t", CreatedBy = "u", UpdatedBy = "u", FileType = FileType.Text, ReferenceType = StrictReferenceKind.Order, ReferenceId = 5 },
            FileServiceHarness.Text());

    [Fact]
    public async Task ListAsync_NonNullableLongReferenceId_Filters()
    {
        var service = Service();
        await SeedAsync(service);

        Assert.Equal(1, (await service.ListAsync(new FileListQuery { ReferenceId = 5 })).TotalItems);
        Assert.Equal(0, (await service.ListAsync(new FileListQuery { ReferenceId = 6 })).TotalItems);
    }

    [Fact]
    public async Task ListAsync_EnumReferenceType_FiltersByName()
    {
        var service = Service();
        await SeedAsync(service);

        Assert.Equal(1, (await service.ListAsync(new FileListQuery { ReferenceType = "order" })).TotalItems);
    }

    [Fact]
    public async Task ListAsync_EnumReferenceType_UnknownName_Is400FilterNotSupported()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Service().ListAsync(new FileListQuery { ReferenceType = "bogus" }));

        Assert.Equal("FILTER_NOT_SUPPORTED", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
    }

    private sealed class StrictContext(DbContextOptions<StrictContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<StrictFile>(e =>
            {
                e.HasKey(f => f.FileId);
                e.Property(f => f.FileId).ValueGeneratedOnAdd();
                e.Property(f => f.FileType).HasConversion(f => (int)f, f => (FileType)f);
                e.Property(f => f.CreatedBy).IsRequired();
                e.Property(f => f.UpdatedBy).IsRequired();
                e.Property(f => f.ExtraProperties).HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<IDictionary<string, object>>(v, (System.Text.Json.JsonSerializerOptions?)null)
                         ?? new Dictionary<string, object>());
            });
    }
}

public class FileUpdateOmittedMembersTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Update_Entity_OmittingReferenceIdAndDuration_KeepsThem()
    {
        var upload = FileServiceHarness.Meta();
        upload.ReferenceId = 7;
        upload.Duration = 12;
        var file = await _h.Service.Upload(upload, FileServiceHarness.Text());

        await _h.Service.Update(file.FileId, new File { Title = "renamed", FileType = FileType.Text });

        var stored = _h.Reload(file.FileId);
        Assert.Equal("renamed", stored.Title);
        Assert.Equal(7, stored.ReferenceId);
        Assert.Equal(12, stored.Duration);
    }
}
