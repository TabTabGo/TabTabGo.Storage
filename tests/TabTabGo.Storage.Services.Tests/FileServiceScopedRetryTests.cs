using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TabTabGo.Data.EF;
using TabTabGo.Storage.Services;
using TabTabGo.Storage.TestKit;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

// A retrying execution strategy runs the scoped UpdateChanges operation twice: the first attempt fails transiently after its save,
// the unit of work rolls back, clears the tracker and runs the operation again. The change must survive the retry (architect R6 finding 1).
public class RetryOnceUnitOfWork(DbContext context)
    : UnitOfWork(context, Microsoft.Extensions.Logging.Abstractions.NullLogger<UnitOfWork>.Instance)
{
    private readonly DbContext _context = context;

    public int Attempts { get; private set; }

    // When set, the next SaveChanges fails with a transient-looking exception (inside the first attempt).
    public bool FailNextSave { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        if (FailNextSave)
        {
            FailNextSave = false;
            throw new TimeoutException("injected transient failure in SaveChanges");
        }

        return base.SaveChangesAsync(cancellationToken);
    }

    // What a retrying execution strategy does: roll back, clear the tracker, run the same operation again.
    public override async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        Attempts++;
        try
        {
            return await base.ExecuteInTransactionAsync(operation, cancellationToken);
        }
        catch (TimeoutException)
        {
            _context.ChangeTracker.Clear();
            Attempts++;
            return await base.ExecuteInTransactionAsync(operation, cancellationToken);
        }
    }
}

public class FileServiceScopedRetryTests : IDisposable
{
    private readonly SqliteStorageFixture _db = new();

    public void Dispose() => _db.Dispose();

    private (HookedFileService Service, RetryOnceUnitOfWork Uow) Build()
    {
        var uow = new RetryOnceUnitOfWork(_db.Context);
        var service = new HookedFileService(uow, new RecordingProvider(), new ImageService(Options.Create(new ImageServiceOptions())),
            Options.Create(new StorageServiceOptions()), new FakeTimeProvider(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero)));
        return (service, uow);
    }

    private string ReloadTitle(long id)
    {
        using var other = _db.NewContext();
        return other.Set<File>().Single(f => f.FileId == id).Title;
    }

    [Fact]
    public async Task Retry_DetachedClientEntity_KeepsTheChange()
    {
        var (service, uow) = Build();
        var file = await service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());
        var client = new File { Title = "changed", ReferenceId = 7, ReferenceType = "Ticket", Comment = "comment" };

        uow.FailNextSave = true;
        await service.UpdateChanges(file.FileId, client, f => f.ReferenceId == 7, CancellationToken.None);

        Assert.Equal(2, uow.Attempts);
        Assert.Equal("changed", ReloadTitle(file.FileId));
    }

    [Fact]
    public async Task Retry_TrackedInstance_LoadEditUpdate_KeepsTheChange()
    {
        var (service, uow) = Build();
        var file = await service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());
        var tracked = await service.GetByKey(file.FileId);
        Assert.NotNull(tracked);
        var state = _db.Context.Entry(tracked!).State;
        if (state == EntityState.Detached)
        {
            tracked = _db.Context.Set<File>().Single(f => f.FileId == file.FileId);
        }

        Assert.NotEqual(EntityState.Detached, _db.Context.Entry(tracked!).State);
        Assert.Same(tracked, _db.Context.Set<File>().Local.Single(f => f.FileId == file.FileId));
        tracked!.Title = "changed";

        uow.FailNextSave = true;
        await service.UpdateChanges(file.FileId, tracked, f => f.ReferenceId == 7, CancellationToken.None);

        Assert.Equal(2, uow.Attempts);
        Assert.Equal("changed", ReloadTitle(file.FileId));
    }

    [Fact]
    public async Task Retry_ChangeThatAlsoChangesTheFileType_StillKeepsTheChange()
    {
        var (service, uow) = Build();
        var file = await service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());
        var client = new File { Title = "changed", ReferenceId = 7, ReferenceType = "Ticket", FileType = TabTabGo.Storage.Enums.FileType.PDF };

        uow.FailNextSave = true;
        await service.UpdateChanges(file.FileId, client, f => f.ReferenceId == 7, CancellationToken.None);

        Assert.Equal("changed", ReloadTitle(file.FileId));
        using var other = _db.NewContext();
        Assert.Equal(TabTabGo.Storage.Enums.FileType.PDF, other.Set<File>().Single(f => f.FileId == file.FileId).FileType);
    }
}

// The scoped update removes the old objects and runs PostUpdate after the unit of work returns. Inside a host transaction that is
// BEFORE the host commits, so a relocation (file type change) is refused there with 409; other scoped updates still work.
public class HostTransactionUnitOfWork(DbContext context) : RetryOnceUnitOfWork(context)
{
    public bool InHostTransaction { get; set; }

    public override bool HasActiveTransaction => InHostTransaction || base.HasActiveTransaction;
}

public class FileServiceScopedHostTransactionTests : IDisposable
{
    private readonly SqliteStorageFixture _db = new();

    public void Dispose() => _db.Dispose();

    private (HookedFileService Service, HostTransactionUnitOfWork Uow, RecordingProvider Storage) Build()
    {
        var uow = new HostTransactionUnitOfWork(_db.Context);
        var storage = new RecordingProvider();
        var service = new HookedFileService(uow, storage, new ImageService(Options.Create(new ImageServiceOptions())),
            Options.Create(new StorageServiceOptions()), new FakeTimeProvider(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero)));
        return (service, uow, storage);
    }

    [Fact]
    public async Task ScopedRelocation_InsideAHostTransaction_Is409_AndChangesNothing()
    {
        var (service, uow, storage) = Build();
        var file = await service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());
        var keys = storage.Keys().OrderBy(k => k).ToList();
        uow.InHostTransaction = true;
        var client = new File { Title = "changed", ReferenceId = 7, ReferenceType = "Ticket", FileType = TabTabGo.Storage.Enums.FileType.PDF };

        var ex = await Assert.ThrowsAsync<TabTabGo.Core.Exceptions.ApiException>(() =>
            service.UpdateChanges(file.FileId, client, f => f.ReferenceId == 7, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.Conflict, ex.HttpStatusCode);
        Assert.Equal(keys, storage.Keys().OrderBy(k => k).ToList());
        using var other = _db.NewContext();
        Assert.Equal(TabTabGo.Storage.Enums.FileType.Text, other.Set<File>().Single(f => f.FileId == file.FileId).FileType);
    }

    [Fact]
    public async Task ScopedUpdate_ThatLeavesTheScope_InsideAHostTransaction_Is404_AndNothingIsSaved()
    {
        // The host owns the transaction and may catch the exception and commit: the move out of scope must never reach the database.
        var (service, uow, _) = Build();
        var file = await service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());
        service.Events.Clear();
        uow.InHostTransaction = true;
        var client = new File { Title = "changed", ReferenceId = 99, ReferenceType = "Ticket", FileType = TabTabGo.Storage.Enums.FileType.Text };

        var ex = await Assert.ThrowsAsync<TabTabGo.Core.Exceptions.ApiException>(() =>
            service.UpdateChanges(file.FileId, client, f => f.ReferenceId == 7, CancellationToken.None));

        Assert.Equal(System.Net.HttpStatusCode.NotFound, ex.HttpStatusCode);
        using var other = _db.NewContext();
        var stored = other.Set<File>().Single(f => f.FileId == file.FileId);
        Assert.Equal(7, stored.ReferenceId);
        Assert.NotEqual("changed", stored.Title);
        Assert.DoesNotContain("PostUpdate", service.Events);
    }

    [Fact]
    public async Task ScopedUpdateWithoutRelocation_InsideAHostTransaction_Works()
    {
        var (service, uow, _) = Build();
        var file = await service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());
        uow.InHostTransaction = true;
        var client = new File { Title = "changed", ReferenceId = 7, ReferenceType = "Ticket", FileType = TabTabGo.Storage.Enums.FileType.Text };

        await service.UpdateChanges(file.FileId, client, f => f.ReferenceId == 7, CancellationToken.None);

        using var other = _db.NewContext();
        Assert.Equal("changed", other.Set<File>().Single(f => f.FileId == file.FileId).Title);
    }
}

// Core 10's public EnsureExtraPropertiesWithinLimits(entity) is the host-facing check; it must apply Storage's own limits.
public class FileServiceExtraPropertiesLimitOverrideTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public void EnsureExtraPropertiesWithinLimits_TooManyKeys_Is400()
    {
        var entity = FileServiceHarness.Meta();
        for (var i = 0; i < new StorageServiceOptions().MaxExtraPropertyKeys + 1; i++)
        {
            entity.ExtraProperties[$"k{i}"] = i;
        }

        var ex = Assert.Throws<TabTabGo.Core.Exceptions.ApiException>(() => _h.Service.EnsureExtraPropertiesWithinLimits(entity));

        Assert.Equal("EXTRA_PROPERTIES_TOO_LARGE", ex.ErrorCode);
    }

    [Fact]
    public void EnsureExtraPropertiesWithinLimits_TooLarge_Is400()
    {
        var entity = FileServiceHarness.Meta();
        entity.ExtraProperties["big"] = new string('x', new StorageServiceOptions().MaxExtraPropertiesBytes + 1);

        var ex = Assert.Throws<TabTabGo.Core.Exceptions.ApiException>(() => _h.Service.EnsureExtraPropertiesWithinLimits(entity));

        Assert.Equal("EXTRA_PROPERTIES_TOO_LARGE", ex.ErrorCode);
    }

    [Fact]
    public void EnsureExtraPropertiesWithinLimits_WithinLimits_Passes()
    {
        var entity = FileServiceHarness.Meta();
        entity.ExtraProperties["a"] = 1;

        _h.Service.EnsureExtraPropertiesWithinLimits(entity);
    }
}

// The first attempt of a scoped update completes (its after-commit steps are queued, the new object is stored) and then the commit
// fails; the unit of work retries the whole operation. The steps of the failed attempt must be dropped: PostUpdate and the removal of
// the old object run once, for the attempt that committed.
public class CommitFailsOnceUnitOfWork(DbContext context)
    : UnitOfWork(context, Microsoft.Extensions.Logging.Abstractions.NullLogger<UnitOfWork>.Instance)
{
    private readonly DbContext _context = context;

    public override async Task<T> ExecuteInTransactionAsync<T>(Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        var first = true;
        try
        {
            return await base.ExecuteInTransactionAsync(async ct =>
            {
                var result = await operation(ct);
                if (first)
                {
                    first = false;
                    throw new TimeoutException("injected transient failure at commit");
                }

                return result;
            }, cancellationToken);
        }
        catch (TimeoutException)
        {
            _context.ChangeTracker.Clear();
            return await base.ExecuteInTransactionAsync(operation, cancellationToken);
        }
    }
}

public class FileServiceScopedCommitRetryTests : IDisposable
{
    private readonly SqliteStorageFixture _db = new();

    public void Dispose() => _db.Dispose();

    [Fact]
    public async Task ARetryAfterAFailedCommit_RunsPostUpdateOnce_RemovesTheOldObjectOnce_AndKeepsTheRelocatedOne()
    {
        var storage = new RecordingProvider();
        var service = new HookedFileService(new CommitFailsOnceUnitOfWork(_db.Context), storage,
            new ImageService(Options.Create(new ImageServiceOptions())), Options.Create(new StorageServiceOptions()),
            new FakeTimeProvider(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero)));
        var file = await service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());
        service.Events.Clear();
        storage.Calls.Clear();
        var oldKey = $"Text/{file.FilePath}";
        var client = new File { Title = "changed", ReferenceId = 7, ReferenceType = "Ticket", FileType = TabTabGo.Storage.Enums.FileType.PDF };

        await service.UpdateChanges(file.FileId, client, f => f.ReferenceId == 7, CancellationToken.None);

        Assert.Equal(1, service.Events.Count(e => e == "PostUpdate"));
        Assert.Equal(1, storage.Calls.Count(c => c == $"Delete:Text/{file.FilePath}"));
        Assert.Equal([$"PDF/{file.FilePath}"], storage.Keys());
        Assert.DoesNotContain(oldKey, storage.Keys());
        using var other = _db.NewContext();
        Assert.Equal(TabTabGo.Storage.Enums.FileType.PDF, other.Set<File>().Single(f => f.FileId == file.FileId).FileType);
    }
}
