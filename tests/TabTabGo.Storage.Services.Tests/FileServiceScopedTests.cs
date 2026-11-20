using System.Linq.Expressions;
using System.Net;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.TestKit;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

// Core 10 adds scoped overloads (Delete / CanDelete / UpdateChanges with a scope expression, the multi-tenant mechanism).
// They must run through the same pipeline as the unscoped ones (restore of storage-owned state, hooks, soft delete,
// rollback), with the scope as an extra filter, and a soft-deleted file must stay invisible to them.
public class FileServiceScopedTests : IDisposable
{
    private const long Mine = 7;
    private const long Theirs = 99;

    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static Expression<Func<File, bool>> InScope => f => f.ReferenceId == Mine;

    private async Task<File> UploadAsync(long reference)
    {
        var meta = FileServiceHarness.Meta();
        meta.ReferenceId = reference;
        var file = await _h.Service.Upload(meta, FileServiceHarness.Text($"n{reference}-{Guid.NewGuid():N}.txt"));
        _h.Service.Events.Clear();
        return file;
    }

    // ---- Delete(id, scope)

    [Fact]
    public async Task DeleteScoped_InScope_SoftDeletes_AndRunsTheHooks()
    {
        var file = await UploadAsync(Mine);

        var deleted = await _h.Service.Delete(file.FileId, InScope, CancellationToken.None);

        Assert.False(deleted.IsEnabled);
        Assert.False(_h.Reload(file.FileId).IsEnabled);
        Assert.Contains("IsValidToDelete", _h.Service.Events);
        Assert.Contains(_h.Service.Events, e => e.StartsWith("PreDelete", StringComparison.Ordinal));
        Assert.Equal(1, _h.Storage.Count);
    }

    [Fact]
    public async Task DeleteScoped_OutOfScope_Is404_AndChangesNothing()
    {
        var file = await UploadAsync(Theirs);

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete(file.FileId, InScope, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        Assert.True(_h.Reload(file.FileId).IsEnabled);
    }

    [Fact]
    public async Task DeleteScoped_AlreadySoftDeleted_Is404()
    {
        var file = await UploadAsync(Mine);
        await _h.Service.Delete(file.FileId);

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete(file.FileId, InScope, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
    }

    [Fact]
    public async Task DeleteScoped_NullScope_IsTheUnscopedDelete()
    {
        var file = await UploadAsync(Theirs);

        await _h.Service.Delete(file.FileId, null, CancellationToken.None);

        Assert.False(_h.Reload(file.FileId).IsEnabled);
    }

    [Fact]
    public async Task DeleteScoped_Veto_Is400_AndChangesNothing()
    {
        var file = await UploadAsync(Mine);
        _h.Service.RejectDelete = true;

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete(file.FileId, InScope, CancellationToken.None));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.True(_h.Reload(file.FileId).IsEnabled);
    }

    [Fact]
    public async Task DeleteScoped_IgnoresEditsMadeToTheTrackedEntity()
    {
        var file = await UploadAsync(Mine);
        var tracked = _h.Db.Context.Set<File>().Local.Single(f => f.FileId == file.FileId);
        var path = tracked.FilePath;
        tracked.FilePath = "Text/someone-elses-object";
        tracked.Title = "tampered";

        await _h.Service.Delete(file.FileId, InScope, CancellationToken.None);

        var stored = _h.Reload(file.FileId);
        Assert.Equal(path, stored.FilePath);
        Assert.NotEqual("tampered", stored.Title);
    }

    // ---- Delete(ids, scope)

    [Fact]
    public async Task DeleteManyScoped_AllInScope_SoftDeletesAll_AndRunsTheCollectionHookOnce()
    {
        var a = await UploadAsync(Mine);
        var b = await UploadAsync(Mine);

        var deleted = (await _h.Service.Delete([a.FileId, b.FileId], InScope, CancellationToken.None)).ToList();

        Assert.Equal(2, deleted.Count);
        Assert.False(_h.Reload(a.FileId).IsEnabled);
        Assert.False(_h.Reload(b.FileId).IsEnabled);
        Assert.Equal(1, _h.Service.Events.Count(e => e == "PreDelete(many)"));
    }

    [Fact]
    public async Task DeleteManyScoped_OneOutOfScope_Is404_AndNothingIsDeleted()
    {
        var mine = await UploadAsync(Mine);
        var theirs = await UploadAsync(Theirs);

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete([mine.FileId, theirs.FileId], InScope, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        Assert.True(_h.Reload(mine.FileId).IsEnabled);
        Assert.True(_h.Reload(theirs.FileId).IsEnabled);
    }

    [Fact]
    public async Task DeleteManyScoped_Veto_Is400_AndChangesNothing()
    {
        var a = await UploadAsync(Mine);
        _h.Service.RejectDelete = true;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete([a.FileId], InScope, CancellationToken.None));

        Assert.True(_h.Reload(a.FileId).IsEnabled);
    }

    // ---- CanDelete(.., scope)

    [Fact]
    public async Task CanDeleteScoped_IsTrueOnlyForAnEnabledDeletableFileInScope()
    {
        var mine = await UploadAsync(Mine);
        var theirs = await UploadAsync(Theirs);
        var gone = await UploadAsync(Mine);
        await _h.Service.Delete(gone.FileId);

        Assert.True(await _h.Service.CanDelete(mine.FileId, InScope, CancellationToken.None));
        Assert.False(await _h.Service.CanDelete(theirs.FileId, InScope, CancellationToken.None));
        Assert.False(await _h.Service.CanDelete(gone.FileId, InScope, CancellationToken.None));
        Assert.False(await _h.Service.CanDelete(12345L, InScope, CancellationToken.None));

        Assert.True(await _h.Service.CanDelete([mine.FileId], InScope, CancellationToken.None));
        Assert.False(await _h.Service.CanDelete([mine.FileId, theirs.FileId], InScope, CancellationToken.None));
        Assert.False(await _h.Service.CanDelete([mine.FileId, gone.FileId], InScope, CancellationToken.None));
    }

    // ---- UpdateChanges(id, entity, scope)

    [Fact]
    public async Task UpdateChangesScoped_InScope_AppliesTheAllowList_AndRunsTheHooks()
    {
        var file = await UploadAsync(Mine);
        var path = file.FilePath;
        var change = FileServiceHarness.Meta();
        change.Title = "renamed";
        change.ReferenceId = Mine;
        change.FilePath = "Text/someone-elses-object";

        var updated = await _h.Service.UpdateChanges(file.FileId, change, InScope, CancellationToken.None);

        var stored = _h.Reload(file.FileId);
        Assert.Equal("renamed", stored.Title);
        Assert.Equal(path, stored.FilePath);
        Assert.Equal("renamed", updated.Title);
        Assert.Contains("IsValidToUpdate", _h.Service.Events);
        Assert.Contains(_h.Service.Events, e => e.StartsWith("PreUpdate", StringComparison.Ordinal));
        Assert.Contains(_h.Service.Events, e => e.StartsWith("PostUpdate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateChangesScoped_OutOfScope_Is404_AndChangesNothing()
    {
        var file = await UploadAsync(Theirs);
        var change = FileServiceHarness.Meta();
        change.Title = "renamed";

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.UpdateChanges(file.FileId, change, InScope, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        Assert.NotEqual("renamed", _h.Reload(file.FileId).Title);
    }

    [Fact]
    public async Task UpdateChangesScoped_AChangeThatLeavesTheScope_Is404_AndIsRolledBack()
    {
        var file = await UploadAsync(Mine);
        var change = FileServiceHarness.Meta();
        change.Title = "renamed";
        change.ReferenceId = Theirs;   // the file would move out of the caller's scope

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.UpdateChanges(file.FileId, change, InScope, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        var stored = _h.Reload(file.FileId);
        Assert.Equal(Mine, stored.ReferenceId);
        Assert.NotEqual("renamed", stored.Title);
        Assert.DoesNotContain(_h.Service.Events, e => e.StartsWith("PostUpdate", StringComparison.Ordinal));
    }

    [Fact]
    public async Task UpdateChangesScoped_AFileTypeChangeThatLeavesTheScope_KeepsTheStoredObject()
    {
        var file = await UploadAsync(Mine);
        var change = FileServiceHarness.Meta(FileType.PDF);
        change.ReferenceId = Theirs;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.UpdateChanges(file.FileId, change, InScope, CancellationToken.None));

        Assert.Equal([$"Text/{file.FilePath}"], _h.Storage.Keys());
        Assert.Equal(FileType.Text, _h.Reload(file.FileId).FileType);
    }

    [Fact]
    public async Task UpdateChangesScoped_PostUpdate_AndTheOldObjectRemoval_RunAfterTheCommit()
    {
        var file = await UploadAsync(Mine);
        var change = FileServiceHarness.Meta(FileType.PDF);
        change.ReferenceId = Mine;
        bool? transactionOpenInPostUpdate = null;
        IReadOnlyList<string>? keysInPostUpdate = null;
        _h.Service.PostUpdateProbe = () =>
        {
            transactionOpenInPostUpdate = _h.Uow.HasActiveTransaction;
            keysInPostUpdate = _h.Storage.Keys().ToList();
        };

        await _h.Service.UpdateChanges(file.FileId, change, InScope, CancellationToken.None);

        Assert.False(transactionOpenInPostUpdate);   // the unit of work has committed before the side effects run
        Assert.Equal([$"PDF/{file.FilePath}"], keysInPostUpdate);   // the old object was removed before PostUpdate
    }

    [Fact]
    public async Task UpdateChangesScoped_NullScope_IsTheUnscopedUpdate()
    {
        var file = await UploadAsync(Theirs);
        var change = FileServiceHarness.Meta();
        change.Title = "renamed";
        change.ReferenceId = Theirs;

        await _h.Service.UpdateChanges(file.FileId, change, null, CancellationToken.None);

        Assert.Equal("renamed", _h.Reload(file.FileId).Title);
    }

    [Fact]
    public async Task UpdateChangesScoped_SoftDeletedFile_Is404()
    {
        var file = await UploadAsync(Mine);
        await _h.Service.Delete(file.FileId);

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            _h.Service.UpdateChanges(file.FileId, FileServiceHarness.Meta(), InScope, CancellationToken.None));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
    }

    // ---- inherited bulk read paths use the service key predicate, so they hide soft-deleted files too

    [Fact]
    public async Task GetDeleteValidation_AndCountByKeys_DoNotSeeSoftDeletedFiles()
    {
        var kept = await UploadAsync(Mine);
        var gone = await UploadAsync(Mine);
        await _h.Service.Delete(gone.FileId);

        var validation = await _h.Service.GetDeleteValidationAsync([kept.FileId, gone.FileId]);
        var count = await _h.Service.CountByKeys([kept.FileId, gone.FileId]);

        Assert.Equal([kept.FileId], validation.Select(v => v.Id));
        Assert.Equal(1, count);
    }
}
