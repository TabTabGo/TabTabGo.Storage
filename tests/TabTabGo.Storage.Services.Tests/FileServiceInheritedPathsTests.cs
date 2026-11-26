using System.Linq.Expressions;
using System.Net;
using Microsoft.AspNetCore.JsonPatch;
using System.Text.Json.Nodes;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.TestKit;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

// Sweep of the public overloads FileService inherits from Core 10 BaseService / BaseReadService: a soft-deleted file must not be
// readable, countable, validatable, updatable or deletable through any of them.
public class FileServiceInheritedPathsTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<(File Kept, File Gone)> SeedAsync()
    {
        var kept = await _h.UploadTextAsync("kept.txt");
        var gone = await _h.UploadTextAsync("gone.txt");
        await _h.Service.Delete(gone.FileId);
        _h.Service.Events.Clear();
        return (kept, gone);
    }

    [Fact]
    public async Task InheritedSingleReads_DoNotSeeASoftDeletedFile()
    {
        var (kept, gone) = await SeedAsync();

        Assert.NotNull(await _h.Service.GetByKey(kept.FileId));
        Assert.Null(await _h.Service.GetByKey(gone.FileId));
        Assert.Null(await _h.Service.GetViewModelByKey(gone.FileId, f => f));
        Assert.True(await _h.Service.Exists(kept.FileId));
        Assert.False(await _h.Service.Exists(gone.FileId));
    }

    [Fact]
    public async Task InheritedBulkReads_DoNotSeeASoftDeletedFile()
    {
        var (kept, gone) = await SeedAsync();

        var list = await _h.Service.GetList(f => true);
        var page = await _h.Service.GetPageList(f => true);
        var validation = await _h.Service.GetDeleteValidationAsync([kept.FileId, gone.FileId]);

        Assert.Equal([kept.FileId], list.Select(f => f.FileId));
        Assert.Equal([kept.FileId], page.Items.Select(f => f.FileId));
        Assert.Equal([kept.FileId], validation.Select(v => v.Id));
        Assert.Equal(1, await _h.Service.CountByKeys([kept.FileId, gone.FileId]));
        Assert.Equal(0, await _h.Service.CountByKeys([gone.FileId]));
        Assert.False(await _h.Service.CanDelete(gone.FileId));
        Assert.False(await _h.Service.CanDelete([kept.FileId, gone.FileId], CancellationToken.None));
    }

    public static IEnumerable<object[]> MutatingOverloads()
    {
        Expression<Func<File, bool>> any = f => true;
        yield return ["Update(entity)", (Func<HookedFileService, long, Task>)((s, id) => s.Update(id, FileServiceHarness.Meta()))];
        yield return ["Update(json)", (Func<HookedFileService, long, Task>)((s, id) => s.Update(id, new JsonObject { ["Title"] = "x" }))];
        yield return ["Update(patch)", (Func<HookedFileService, long, Task>)((s, id) =>
        {
            var patch = new JsonPatchDocument<File>();
            patch.Replace(f => f.Title, "x");
            return s.Update(id, patch);
        })];
        yield return ["UpdateChanges", (Func<HookedFileService, long, Task>)((s, id) => s.UpdateChanges(id, FileServiceHarness.Meta(), CancellationToken.None))];
        yield return ["UpdateChanges(scope)", (Func<HookedFileService, long, Task>)((s, id) => s.UpdateChanges(id, FileServiceHarness.Meta(), any, CancellationToken.None))];
        yield return ["Replace", (Func<HookedFileService, long, Task>)((s, id) => s.Replace(id, FileServiceHarness.Text("r.txt", "r")))];
        yield return ["Delete(id)", (Func<HookedFileService, long, Task>)((s, id) => s.Delete(id))];
        yield return ["Delete(id,scope)", (Func<HookedFileService, long, Task>)((s, id) => s.Delete(id, any, CancellationToken.None))];
        yield return ["Delete(ids,scope)", (Func<HookedFileService, long, Task>)((s, id) => s.Delete([id], any, CancellationToken.None))];
    }

    [Theory]
    [MemberData(nameof(MutatingOverloads))]
    public async Task EveryMutatingOverload_Treats_ASoftDeletedFile_AsNotFound(string name, Func<HookedFileService, long, Task> run)
    {
        var (_, gone) = await SeedAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() => run(_h.Service, gone.FileId));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        Assert.False(_h.Reload(gone.FileId).IsEnabled);
        Assert.True(name.Length > 0);
    }

    [Fact]
    public async Task BulkSoftDelete_UnscopedAndScoped_SkipOrRefuseASoftDeletedId()
    {
        var (kept, gone) = await SeedAsync();

        var unscoped = (await _h.Service.Delete([gone.FileId], CancellationToken.None)).ToList();   // unscoped: ignored, as in 8.x
        Assert.Empty(unscoped);

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete([kept.FileId, gone.FileId], f => true, CancellationToken.None));
        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        Assert.True(_h.Reload(kept.FileId).IsEnabled);
    }

    // Host hooks fire on every path that changes a row (spec: gate parity); the table lives in FileServiceGateParityTests.
    [Fact]
    public async Task ScopedPaths_FireTheHostHooks_InTheSameOrderAsTheUnscopedOnes()
    {
        var a = await _h.UploadTextAsync("a.txt");
        var b = await _h.UploadTextAsync("b.txt");

        await _h.Service.Delete(a.FileId);
        var unscoped = _h.Service.Events.ToList();
        _h.Service.Events.Clear();
        await _h.Service.Delete(b.FileId, f => true, CancellationToken.None);
        var scoped = _h.Service.Events.ToList();

        Assert.Equal(unscoped, scoped);
        Assert.Equal(["IsValidToDelete", "PreDelete", "PostDelete"], scoped);
    }
}
