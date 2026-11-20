using Microsoft.AspNetCore.JsonPatch;
using Microsoft.EntityFrameworkCore;
using System.Text.Json.Nodes;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.TestKit;
using TabTabGo.Storage.Testing;
using Xunit;

namespace TabTabGo.Storage.Services.Tests;

// C: a tracked reference navigation edited to point at another principal is turned into a foreign-key change by EF at save
// time, even though the scalar was restored. The service refuses to proceed (409) instead of persisting or judging it.
public class FileServiceNavigationTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("DataSource=:memory:");
    private readonly NavigationContext _context;
    private readonly TenantFileService _service;

    public FileServiceNavigationTests()
    {
        _connection.Open();
        _context = new NavigationContext(new DbContextOptionsBuilder<NavigationContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _context.AddRange(new Tenant { Id = 1, Name = "attacker" }, new Tenant { Id = 2, Name = "victim" });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        _service = new TenantFileService(new TestUnitOfWork(_context), new InMemoryStorageProvider());
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<TenantFile> VictimAsync()
    {
        _service.AllowedTenant = 2;
        var file = await _service.Upload(new TenantFile { Title = "t", CreatedBy = "owner", UpdatedBy = "owner", FileType = FileType.Text, TenantId = 2 },
            FileServiceHarness.Text());
        _service.GateSawTenants.Clear();
        return file;
    }

    private TenantFile Tracked(long id) => _context.Set<TenantFile>().Local.Single(f => f.FileId == id);

    private long StoredTenant(long id)
    {
        using var other = new NavigationContext(new DbContextOptionsBuilder<NavigationContext>().UseSqlite(_connection).Options);
        return other.Set<TenantFile>().Single(f => f.FileId == id).TenantId;
    }

    private void PointTheNavigationAtTheAttackersTenant(long id) =>
        Tracked(id).Tenant = _context.Set<Tenant>().Single(t => t.Id == 1);

    public static IEnumerable<object[]> Operations()
    {
        yield return ["entity", (Func<TenantFileService, long, Task>)((s, id) => s.Update(id, new TenantFile { Title = "t", FileType = FileType.Text }))];
        yield return ["json", (Func<TenantFileService, long, Task>)((s, id) => s.Update(id, new JsonObject { ["Title"] = "x" }))];
        yield return ["patch", (Func<TenantFileService, long, Task>)((s, id) =>
        {
            var patch = new JsonPatchDocument<TenantFile>();
            patch.Replace(f => f.Title, "x");
            return s.Update(id, patch);
        })];
        yield return ["updateChanges", (Func<TenantFileService, long, Task>)((s, id) => s.UpdateChanges(id, new TenantFile { Title = "t", FileType = FileType.Text }, CancellationToken.None))];
        yield return ["replace", (Func<TenantFileService, long, Task>)((s, id) => s.Replace(id, FileServiceHarness.Text("n.txt", "new")))];
        yield return ["softDelete", (Func<TenantFileService, long, Task>)((s, id) => s.Delete(id))];
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public async Task AnEditedNavigation_IsRefused_AndTheForeignKeyIsNotChanged(string name, Func<TenantFileService, long, Task> run)
    {
        var victim = await VictimAsync();
        _service.AllowedTenant = 1;   // the attacker's own tenant
        PointTheNavigationAtTheAttackersTenant(victim.FileId);

        var ex = await Assert.ThrowsAsync<ApiException>(() => run(_service, victim.FileId));

        Assert.Equal("TRACKED_STATE_CONFLICT", ex.ErrorCode);
        Assert.Equal(System.Net.HttpStatusCode.Conflict, ex.HttpStatusCode);
        Assert.Equal(2, StoredTenant(victim.FileId));
        Assert.True(name.Length > 0);
    }

    [Fact]
    public async Task AnUntouchedNavigation_Loaded_IsFine()
    {
        var victim = await VictimAsync();
        Tracked(victim.FileId).Tenant = _context.Set<Tenant>().Single(t => t.Id == 2);   // fix-up to the same principal

        await _service.Update(victim.FileId, new TenantFile { Title = "ok", FileType = FileType.Text });

        Assert.Equal(2, StoredTenant(victim.FileId));
    }

    private sealed class NavigationContext(DbContextOptions<NavigationContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Tenant>(t => t.HasKey(x => x.Id));
            modelBuilder.Entity<TenantFile>(e =>
            {
                e.HasKey(f => f.FileId);
                e.HasOne(f => f.Tenant).WithMany().HasForeignKey(f => f.TenantId).IsRequired().OnDelete(DeleteBehavior.Restrict);
                e.Property(f => f.FileId).ValueGeneratedOnAdd();
                e.Property(f => f.FileType).HasConversion(f => (int)f, f => (FileType)f);
                e.Property(f => f.CreatedBy).IsRequired();
                e.Property(f => f.UpdatedBy).IsRequired();
                e.Property(f => f.ExtraProperties).HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<IDictionary<string, object?>>(v, (System.Text.Json.JsonSerializerOptions?)null)
                         ?? new Dictionary<string, object?>());
            });
        }
    }
}
