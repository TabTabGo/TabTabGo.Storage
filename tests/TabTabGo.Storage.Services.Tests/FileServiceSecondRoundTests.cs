using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Testing;
using TabTabGo.Core.Data;
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

// N1: the update gate runs on BOTH states - the stored row and the stored row with the client's changes - and rejects if either fails.
public class FileServiceGateBothStatesTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<File> VictimAsync(long referenceId = 99)
    {
        var meta = FileServiceHarness.Meta();
        meta.ReferenceId = referenceId;
        var file = await _h.Service.Upload(meta, FileServiceHarness.Text());
        _h.Service.Events.Clear();
        _h.Storage.Calls.Clear();
        return file;
    }

    private long? StoredReferenceId(long id) => _h.Reload(id).ReferenceId;

    // PoC: a gate "entity.ReferenceId == 7" let the attacker take over a record owned by 99 by sending ReferenceId = 7.
    public static IEnumerable<object[]> Takeovers()
    {
        yield return ["entity", (Func<FileServiceHarness, long, Task>)((h, id) => h.Service.Update(id, new File { ReferenceId = 7, FileType = FileType.Text }))];
        yield return ["json", (Func<FileServiceHarness, long, Task>)((h, id) => h.Service.Update(id, new JsonObject { ["ReferenceId"] = 7 }))];
        yield return ["patch", (Func<FileServiceHarness, long, Task>)((h, id) =>
        {
            var patch = new JsonPatchDocument<File>();
            patch.Replace(f => f.ReferenceId, 7L);
            return h.Service.Update(id, patch);
        })];
        yield return ["updateChanges", (Func<FileServiceHarness, long, Task>)((h, id) => h.Service.UpdateChanges(id, new File { ReferenceId = 7, FileType = FileType.Text }, CancellationToken.None))];
    }

    [Theory]
    [MemberData(nameof(Takeovers))]
    public async Task ReassigningAForeignRecord_IsRejected(string name, Func<FileServiceHarness, long, Task> attack)
    {
        var victim = await VictimAsync();
        _h.Service.RequireReferenceId = 7;

        var ex = await Assert.ThrowsAsync<ApiException>(() => attack(_h, victim.FileId));

        Assert.Equal("FILE_INVALID", ex.ErrorCode);
        Assert.Equal(99, StoredReferenceId(victim.FileId));
        Assert.True(name.Length > 0);
    }

    [Fact]
    public async Task Replace_OnAForeignRecord_IsRejected()
    {
        var victim = await VictimAsync();
        _h.Service.RequireReferenceId = 7;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Replace(victim.FileId, FileServiceHarness.Text("n.txt", "new")));

        Assert.Equal(victim.FilePath, _h.Reload(victim.FileId).FilePath);
        Assert.Equal(1, _h.Storage.Count);
    }

    [Fact]
    public async Task MovingAnOwnRecordOutOfScope_IsRejected_TheMergedStateFails()
    {
        var own = await VictimAsync(referenceId: 7);
        _h.Service.RequireReferenceId = 7;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(own.FileId, new File { ReferenceId = 8, FileType = FileType.Text }));
        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(own.FileId, new JsonObject { ["ReferenceId"] = 8 }));

        Assert.Equal(7, StoredReferenceId(own.FileId));
    }

    [Fact]
    public async Task AnOwnRecord_CanBeEditedWithinScope()
    {
        var own = await VictimAsync(referenceId: 7);
        _h.Service.RequireReferenceId = 7;

        await _h.Service.Update(own.FileId, new File { Title = "fine", ReferenceId = 7, FileType = FileType.Text });
        await _h.Service.Update(own.FileId, new JsonObject { ["Comment"] = "fine too" });
        await _h.Service.Replace(own.FileId, FileServiceHarness.Text("n.txt", "new"));

        Assert.Equal("fine", _h.Reload(own.FileId).Title);
    }

    // The tracked instance of the victim carries the attacker's value from an earlier request on the same context.
    [Fact]
    public async Task ATrackedReferenceId_ThatWasEditedToPassTheGate_StillFails()
    {
        var victim = await VictimAsync();
        _h.Service.RequireReferenceId = 7;
        _h.Db.Context.Set<File>().Local.Single(f => f.FileId == victim.FileId).ReferenceId = 7;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(victim.FileId, new File { Title = "x", FileType = FileType.Text }));

        Assert.Equal(99, StoredReferenceId(victim.FileId));
    }

    [Fact]
    public async Task TitleVeto_StillRejects_AndPreUpdateStillReceivesTheStoredRow()
    {
        var own = await VictimAsync(referenceId: 7);
        _h.Service.VetoTitle = "forbidden";

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(own.FileId, new File { Title = "forbidden", FileType = FileType.Text }));
        _h.Service.VetoTitle = null;
        await _h.Service.Update(own.FileId, new File { Title = "ok", ReferenceId = 55, FileType = FileType.Text });

        Assert.Equal(7, _h.Service.PreUpdateCurrentReferenceId);   // current = stored row, not the client's 55
        Assert.Equal(55, StoredReferenceId(own.FileId));
    }

    [Fact]
    public async Task BothStatesAreEvaluated_ByDefault()
    {
        var own = await VictimAsync(referenceId: 7);

        await _h.Service.Update(own.FileId, new File { Title = "x", FileType = FileType.Text });

        Assert.Equal(2, _h.Service.Events.Count(e => e == "IsValidToUpdate"));
    }
}

// A consumer entity with its own ownership column, like Techio's AssetFile or a multi-tenant host entity.
public class TenantFile : Entity, IFile
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

    public long TenantId { get; set; }

    public string? OwnerEmail { get; set; }

    public Tenant? Tenant { get; set; }
}

public class Tenant
{
    public long Id { get; set; }

    public string Name { get; set; } = string.Empty;
}

public sealed class TenantFileService(IUnitOfWork uow, InMemoryStorageProvider storage)
    : FileService<TenantFile>(uow, new FakeLogger<FileService<TenantFile>>(), storage, new ImageService())
{
    public long AllowedTenant { get; set; } = 1;

    public List<long> GateSawTenants { get; } = new();

    public override bool IsValidToUpdate(TenantFile? entity, out string validationErrorMessage)
    {
        GateSawTenants.Add(entity?.TenantId ?? -1);
        validationErrorMessage = "wrong tenant";
        return entity?.TenantId == AllowedTenant;
    }
}

// N2: EVERY mapped scalar of TFile (including host columns) is restored from the database before the allow-list is applied.
public class FileServiceFullRestoreTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TenantContext _context;
    private readonly InMemoryStorageProvider _storage = new();
    private readonly TenantFileService _service;

    public FileServiceFullRestoreTests()
    {
        _connection.Open();
        _context = new TenantContext(new DbContextOptionsBuilder<TenantContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
        _context.AddRange(new Tenant { Id = 1, Name = "attacker" }, new Tenant { Id = 2, Name = "victim" });
        _context.SaveChanges();
        _context.ChangeTracker.Clear();
        _service = new TenantFileService(new TestUnitOfWork(_context), _storage);
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private async Task<TenantFile> UploadAsync(long tenant)
    {
        _service.AllowedTenant = tenant;
        var file = await _service.Upload(new TenantFile { Title = "t", CreatedBy = "owner", UpdatedBy = "owner", FileType = FileType.Text, TenantId = tenant, OwnerEmail = "owner@x" },
            FileServiceHarness.Text());
        _service.GateSawTenants.Clear();
        return file;
    }

    private TenantFile Reload(long id)
    {
        using var other = new TenantContext(new DbContextOptionsBuilder<TenantContext>().UseSqlite(_connection).Options);
        return other.Set<TenantFile>().Single(f => f.FileId == id);
    }

    private TenantFile Tracked(long id) => _context.Set<TenantFile>().Local.Single(f => f.FileId == id);

    [Fact]
    public async Task ATrackedTenantId_EditedToPassTheGate_IsRestored_AndTheVictimStaysRejected()
    {
        var victim = await UploadAsync(tenant: 2);
        _service.AllowedTenant = 1;
        Tracked(victim.FileId).TenantId = 1;   // attacker tenant

        var ex = await Assert.ThrowsAsync<ApiException>(() => _service.Update(victim.FileId, new TenantFile { Title = "x", FileType = FileType.Text }));

        Assert.Equal("FILE_INVALID", ex.ErrorCode);
        Assert.All(_service.GateSawTenants, t => Assert.Equal(2, t));
        Assert.Equal(2, Reload(victim.FileId).TenantId);
        Assert.Equal("t", Reload(victim.FileId).Title);
    }

    [Fact]
    public async Task HostColumns_UpdatedBy_AndExtraProperties_CannotBeSmuggledThroughTheTrackedInstance()
    {
        var own = await UploadAsync(tenant: 1);
        var tracked = Tracked(own.FileId);
        tracked.TenantId = 9;
        tracked.OwnerEmail = "attacker@x";
        tracked.UpdatedBy = "attacker";
        tracked.UpdatedDate = new DateTimeOffset(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);
        tracked.ExtraProperties["role"] = "admin";
        tracked.Title = "tampered";

        await _service.Update(own.FileId, new TenantFile { Title = "t", Comment = "ok", FileType = FileType.Text });

        var row = Reload(own.FileId);
        Assert.Equal(1, row.TenantId);
        Assert.Equal("owner@x", row.OwnerEmail);
        Assert.Equal("owner", row.UpdatedBy);
        Assert.NotEqual(2001, row.UpdatedDate.Year);
        Assert.False(row.ExtraProperties.ContainsKey("role"));
        Assert.Equal("t", row.Title);
        Assert.Equal("ok", row.Comment);
    }

    [Fact]
    public async Task EveryMutatingOverload_RestoresHostColumns()
    {
        var own = await UploadAsync(tenant: 1);
        foreach (var run in new Func<Task>[]
                 {
                     () => _service.Update(own.FileId, new TenantFile { Title = "a", FileType = FileType.Text }),
                     () => _service.Update(own.FileId, new JsonObject { ["Title"] = "b" }),
                     () => _service.UpdateChanges(own.FileId, new TenantFile { Title = "c", FileType = FileType.Text }, CancellationToken.None),
                     () => _service.Replace(own.FileId, FileServiceHarness.Text("n.txt", "x")),
                 })
        {
            Tracked(own.FileId).TenantId = 5;
            Tracked(own.FileId).OwnerEmail = "attacker@x";

            await run();

            Assert.Equal(1, Reload(own.FileId).TenantId);
            Assert.Equal("owner@x", Reload(own.FileId).OwnerEmail);
        }
    }

    private sealed class TenantContext(DbContextOptions<TenantContext> options) : DbContext(options)
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

// N3: Purge runs the single-entity hooks and its own validation hook.
public class FileServicePurgeHookTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Purge_Id_RunsTheSingleEntityHooks()
    {
        var file = await _h.UploadTextAsync();

        await _h.Service.Purge(file.FileId);

        Assert.Equal(["IsValidToPurge", "IsValidToDelete", "PreDelete", "PostDelete"], _h.Service.Events);
    }

    [Fact]
    public async Task AHostVetoInPreDelete_BlocksPurge_AndNothingIsRemoved()
    {
        var file = await _h.UploadTextAsync();
        _h.Service.RejectPreDelete = true;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Purge(file.FileId));
        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Purge([file.FileId]));
        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete(f => f.FileId == file.FileId));

        Assert.Equal(1, _h.RowCount());
        Assert.Equal(1, _h.Storage.Count);
    }

    [Fact]
    public async Task IsValidToPurge_CanBeStricterThanIsValidToDelete()
    {
        var file = await _h.UploadTextAsync();
        _h.Service.RejectPurge = true;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Purge(file.FileId));
        await _h.Service.Delete(file.FileId);   // the soft delete is still allowed

        Assert.False(_h.Reload(file.FileId).IsEnabled);
        Assert.Equal(1, _h.Storage.Count);
    }

    [Fact]
    public async Task Purge_Ids_FansOutTheSingleEntityHooks()
    {
        var a = await _h.UploadTextAsync();
        var b = await _h.UploadTextAsync();

        await _h.Service.Purge([a.FileId, b.FileId]);

        Assert.Equal(2, _h.Service.Events.Count(e => e == "PreDelete"));
        Assert.Equal(2, _h.Service.Events.Count(e => e == "PostDelete"));
        Assert.Contains("PreDelete(many)", _h.Service.Events);
    }
}

// N5: the create gate sees the real content, not what the metadata claims.
public class FileServiceCreateGateTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task TheGate_SeesTheRealExtensionNameAndSize_NotTheMetadataClaims()
    {
        var meta = FileServiceHarness.Meta();
        meta.FileExtension = ".pdf";
        meta.OriginalFileName = "invoice.pdf";
        meta.FilePath = "../../elsewhere";
        meta.FileSize = 1;

        await _h.Service.Upload(meta, FileServiceHarness.Text("x.docx", "twelve bytes"));

        var seen = _h.Service.CreateSeen!.Value;
        Assert.Equal(".docx", seen.Extension);
        Assert.Equal("x.docx", seen.Name);
        Assert.Equal(string.Empty, seen.Path);
        Assert.Equal(12, seen.Size);
    }

    [Fact]
    public async Task ExtraPropertiesFromTheMetadata_AreStripped_ByDefault()
    {
        var meta = FileServiceHarness.Meta();
        meta.ExtraProperties["role"] = "admin";

        var file = await _h.Service.Upload(meta, FileServiceHarness.Text());

        Assert.Empty(_h.Reload(file.FileId).ExtraProperties);
        Assert.Equal(0, _h.Service.CreateSeen!.Value.Extra);
    }

    [Fact]
    public async Task ExtraPropertiesFromTheMetadata_AreKept_WhenTheHostOptsIn()
    {
        using var h = new FileServiceHarness(o => o.AllowMetadataExtraProperties = true);
        var meta = FileServiceHarness.Meta();
        meta.ExtraProperties["source"] = "scanner";

        var file = await h.Service.Upload(meta, FileServiceHarness.Text());

        Assert.Equal("scanner", h.Reload(file.FileId).ExtraProperties["source"]!.ToString());
    }

    [Fact]
    public void TheOption_DefaultsToFalse() => Assert.False(new StorageServiceOptions().AllowMetadataExtraProperties);
}

// N6: "delete" means "hide": soft-deleted rows do not show through the inherited Core reads either.
public class FileServiceSoftDeleteReadsTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task InheritedReads_DoNotReturnSoftDeletedFiles()
    {
        var gone = await _h.UploadTextAsync();
        var kept = await _h.UploadTextAsync();
        await _h.Service.Delete(gone.FileId);
        var goneId = gone.FileId;

        Assert.Null(await _h.Service.GetByKey(goneId));
        Assert.NotNull(await _h.Service.GetByKey(kept.FileId));
        Assert.False(await _h.Service.Exists(goneId));
        Assert.True(await _h.Service.Exists(kept.FileId));
        Assert.Equal([kept.FileId], (await _h.Service.GetList(f => true)).Select(f => f.FileId));
        var page = await _h.Service.GetPageList(f => true, null, 0, 50);
        Assert.Equal([kept.FileId], page.Items.Select(f => f.FileId));
        Assert.Null(await _h.Service.GetViewModelByKey(goneId, f => new object()));
    }

    [Fact]
    public async Task GetKeyPredicate_ExcludesDisabledRows()
    {
        var file = await _h.UploadTextAsync();
        file.IsEnabled = false;

        Assert.False(_h.Service.GetKeyPredicate(file.FileId).Compile()(file));
    }

    [Fact]
    public void TheODataQueryable_IsFilteredToo()
    {
        var method = typeof(FileService<File>).GetMethods().Single(m => m.Name == "GetQueryable" && m.GetParameters().Length == 7);

        Assert.Equal(typeof(FileService<File>), method.DeclaringType);
    }

    [Fact]
    public async Task Purge_StillFindsDisabledRows()
    {
        var file = await _h.UploadTextAsync();
        await _h.Service.Delete(file.FileId);

        await _h.Service.Purge(file.FileId);

        Assert.Equal(0, _h.RowCount());
    }
}

public class FileServiceMiscHardeningTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    // N7
    [Fact]
    public async Task IsCompressed_IsFalse_AfterUpload_WhateverTheClientSent()
    {
        var meta = FileServiceHarness.Meta();
        meta.IsCompressed = true;

        var file = await _h.Service.Upload(meta, FileServiceHarness.Text());

        Assert.False(_h.Reload(file.FileId).IsCompressed);
    }

    // N8
    [Fact]
    public async Task Upload_FileName_ZeroWidthAndLineSeparators_AreStripped()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("a\u200Bb\u200Cc\u200Dd\u2060e\uFEFFf\u2028g\u2029h.txt"));

        Assert.Equal("abcdefgh.txt", file.OriginalFileName);
    }
}
