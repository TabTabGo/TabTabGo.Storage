using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.TestKit;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

// Soft delete (Core and 6.x semantics), hard delete only through Delete(query) and Purge, IsEnabled honoured everywhere.
public class FileServiceSoftDeleteTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Delete_ById_Missing_Or_AlreadySoftDeleted_404()
    {
        var file = await _h.UploadTextAsync();
        await _h.Service.Delete(file.FileId);

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete(file.FileId));
        var ex2 = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete(9999));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ex2.HttpStatusCode);
    }

    [Fact]
    public async Task SoftDeletedFile_IsHiddenFromEveryReadAndMutation()
    {
        var file = await _h.UploadImageAsync();
        var id = file.FileId;
        await _h.Service.Delete(id);

        var calls = new Func<Task>[]
        {
            () => _h.Service.GetFile(id),
            () => _h.Service.GetImage(id),
            () => _h.Service.GetImage(id, ImageSize.ThumbSmall),
            () => _h.Service.Update(id, FileServiceHarness.Meta(FileType.Image)),
            () => _h.Service.Update(id, new JsonObject { ["Title"] = "x" }),
            () => _h.Service.UpdateChanges(id, FileServiceHarness.Meta(FileType.Image), CancellationToken.None),
            () => _h.Service.Replace(id, FileServiceHarness.PngUpload()),
            () => _h.Service.Delete(id),
            () => _h.Service.Delete([id], CancellationToken.None),
        };

        foreach (var call in calls)
        {
            var ex = await Assert.ThrowsAsync<ApiException>(call);
            Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        }

        Assert.Equal(4, _h.Storage.Count);
    }

    [Fact]
    public async Task Delete_Ids_SkipsAlreadySoftDeleted()
    {
        var a = await _h.UploadTextAsync();
        var b = await _h.UploadTextAsync();
        await _h.Service.Delete(a.FileId);

        var deleted = (await _h.Service.Delete([a.FileId, b.FileId], CancellationToken.None)).ToList();

        Assert.Equal([b.FileId], deleted.Select(f => f.FileId));
    }

    [Fact]
    public async Task Purge_Id_HardDeletes_AndRemovesObjectsAndThumbnails()
    {
        var image = await _h.UploadImageAsync();

        await _h.Service.Purge(image.FileId);

        Assert.Equal(0, _h.RowCount());
        Assert.Equal(0, _h.Storage.Count);
    }

    [Fact]
    public async Task Purge_Ids_HardDeletesOnlyThose()
    {
        var a = await _h.UploadTextAsync();
        var b = await _h.UploadTextAsync();
        var keep = await _h.UploadTextAsync();

        var purged = (await _h.Service.Purge([a.FileId, b.FileId])).ToList();

        Assert.Equal(2, purged.Count);
        Assert.Equal(1, _h.RowCount());
        Assert.Equal([$"Text/{keep.FilePath}"], _h.Storage.Keys());
    }

    [Fact]
    public async Task Purge_AlsoRemovesSoftDeletedFiles()
    {
        var file = await _h.UploadImageAsync();
        await _h.Service.Delete(file.FileId);

        await _h.Service.Purge(file.FileId);

        Assert.Equal(0, _h.RowCount());
        Assert.Equal(0, _h.Storage.Count);
    }

    [Fact]
    public async Task Purge_Missing_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<ApiException>(() => _h.Service.Purge(404))).HttpStatusCode);

    [Fact]
    public async Task Purge_RunsDeleteGates()
    {
        var file = await _h.UploadTextAsync();
        _h.Service.RejectDelete = true;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Purge(file.FileId));
        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Purge([file.FileId]));

        Assert.Equal(1, _h.RowCount());
        Assert.Equal(1, _h.Storage.Count);
    }

    [Fact]
    public void Purge_IsNotOnTheInterface() =>
        Assert.Null(typeof(IFileService<File>).GetMethod("Purge"));

    [Fact]
    public async Task DeleteQuery_StillHardDeletes_AndRemovesObjects()
    {
        var image = await _h.UploadImageAsync();
        var id = image.FileId;

        await _h.Service.Delete(f => f.FileId == id);

        Assert.Equal(0, _h.RowCount());
        Assert.Equal(0, _h.Storage.Count);
    }
}

// H1: a caller (or an earlier request on the same context) mutates the tracked entity; storage-owned fields must come
// from the database, never from the tracked instance.
public class FileServiceMassAssignmentTests : IDisposable
{
    private readonly FileServiceHarness _h = new();
    private File _a = null!;
    private File _b = null!;
    private string _aPath = null!;
    private string _bPath = null!;

    public void Dispose() => _h.Dispose();

    private async Task SetUp()
    {
        _a = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("a.txt", "A-content"));
        _b = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("b.txt", "B-content"));
        _aPath = _a.FilePath;
        _bPath = _b.FilePath;
        _h.Storage.Calls.Clear();
    }

    // The attacker's request left these values on the tracked instance of A (same DbContext identity map).
    private void Tamper()
    {
        var tracked = _h.Db.Context.Set<File>().Local.Single(f => f.FileId == _a.FileId);
        tracked.FilePath = _bPath;
        tracked.FileExtension = ".html";
        tracked.OriginalFileName = "evil.html";
        tracked.OriginalMediaType = "text/html";
        tracked.FileSize = 1;
        tracked.FileType = FileType.Image;
        tracked.CreatedBy = "attacker";
    }

    private async Task AssertAIntact(bool contentReplaced = false)
    {
        var row = _h.Reload(_a.FileId);
        Assert.NotEqual(_bPath, row.FilePath);
        Assert.Equal("tester", row.CreatedBy);
        Assert.Equal(FileType.Text, row.FileType);
        Assert.Equal(".txt", row.FileExtension);
        if (!contentReplaced)
        {
            Assert.Equal(_aPath, row.FilePath);
            Assert.Equal("a.txt", row.OriginalFileName);
            Assert.Equal("text/plain", row.OriginalMediaType);
            Assert.Equal(9, row.FileSize);
            await using var a = await _h.Service.GetFile(_a.FileId);
            Assert.Equal("A-content", new StreamReader(a.Stream).ReadToEnd());
        }

        await using var b = await _h.Service.GetFile(_b.FileId);
        Assert.Equal("B-content", new StreamReader(b.Stream).ReadToEnd());
    }

    [Fact]
    public async Task Update_Entity_RestoresStorageOwnedFields()
    {
        await SetUp();
        Tamper();

        await _h.Service.Update(_a.FileId, new File { Title = "ok", FileType = FileType.Text });

        await AssertAIntact();
        Assert.Equal("ok", _h.Reload(_a.FileId).Title);
    }

    [Fact]
    public async Task Update_Json_RestoresStorageOwnedFields()
    {
        await SetUp();
        Tamper();

        await _h.Service.Update(_a.FileId, new JsonObject { ["Title"] = "ok" });

        await AssertAIntact();
    }

    [Fact]
    public async Task UpdateChanges_CannotPersistTrackedTampering()
    {
        await SetUp();
        Tamper();

        try
        {
            await _h.Service.UpdateChanges(_a.FileId, new File { Title = "ok", FileType = FileType.Text }, CancellationToken.None);
        }
        catch (ApiException)
        {
            // Rejecting the call is also acceptable; persisting the tampering is not.
        }

        await AssertAIntact();
    }

    [Fact]
    public async Task UpdateChanges_AppliesOnlyTheAllowList()
    {
        await SetUp();

        await _h.Service.UpdateChanges(_a.FileId, new File { Title = "via changes", FilePath = "../../x", FileType = FileType.Text }, CancellationToken.None);

        var row = _h.Reload(_a.FileId);
        Assert.Equal("via changes", row.Title);
        Assert.Equal(_aPath, row.FilePath);
    }

    [Fact]
    public async Task Replace_IgnoresTamperedState_AndLeavesOtherFilesAlone()
    {
        await SetUp();
        Tamper();

        await _h.Service.Replace(_a.FileId, FileServiceHarness.Text("a2.txt", "A-new"));

        await AssertAIntact(contentReplaced: true);
        Assert.Contains($"Text/{_bPath}", _h.Storage.Keys());
        Assert.DoesNotContain(_h.Storage.Calls, c => c == $"Delete:Text/{_bPath}" || c == $"Delete:Image/{_bPath}");
        var row = _h.Reload(_a.FileId);
        Assert.Equal("a2.txt", row.OriginalFileName);
        Assert.Equal(".txt", row.FileExtension);
    }

    [Fact]
    public async Task Delete_ById_Soft_IgnoresTamperedState()
    {
        await SetUp();
        Tamper();

        await _h.Service.Delete(_a.FileId);

        Assert.Equal(2, _h.Storage.Count);
        Assert.DoesNotContain(_h.Storage.Calls, c => c.StartsWith("Delete:", StringComparison.Ordinal));
        Assert.False(_h.Reload(_a.FileId).IsEnabled);
        Assert.Equal("tester", _h.Reload(_a.FileId).CreatedBy);
    }

    [Fact]
    public async Task DeleteQuery_RemovesTheRealObject_NotTheTamperedPath()
    {
        await SetUp();
        Tamper();
        var id = _a.FileId;

        await _h.Service.Delete(f => f.FileId == id);

        Assert.Equal([$"Text/{_bPath}"], _h.Storage.Keys());
        await using var b = await _h.Service.GetFile(_b.FileId);
        Assert.Equal("B-content", new StreamReader(b.Stream).ReadToEnd());
    }

    [Fact]
    public async Task Purge_RemovesTheRealObject_NotTheTamperedPath()
    {
        await SetUp();
        Tamper();

        await _h.Service.Purge(_a.FileId);

        Assert.Equal([$"Text/{_bPath}"], _h.Storage.Keys());
    }

    [Fact]
    public async Task PurgeIds_RemovesTheRealObject_NotTheTamperedPath()
    {
        await SetUp();
        Tamper();

        await _h.Service.Purge([_a.FileId]);

        Assert.Equal([$"Text/{_bPath}"], _h.Storage.Keys());
    }

    [Fact]
    public async Task GetFile_AfterTampering_ServesTheRealContent()
    {
        await SetUp();
        Tamper();

        await using var a = await _h.Service.GetFile(_a.FileId);

        Assert.Equal("A-content", new StreamReader(a.Stream).ReadToEnd());
    }
}

public class FileServiceValidationHardeningTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    // Gate parity (X-3 follow-up): Core validates the INCOMING entity.
    [Fact]
    public async Task Update_Entity_ValidatorSeesTheClientValues()
    {
        var file = await _h.UploadTextAsync();
        _h.Service.VetoTitle = "forbidden title";

        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            _h.Service.Update(file.FileId, new File { Title = "forbidden title", FileType = FileType.Text }));

        Assert.Equal("FILE_INVALID", ex.ErrorCode);
        Assert.Equal("title", _h.Reload(file.FileId).Title);
    }

    // FileType validation
    [Fact]
    public async Task Upload_UndefinedFileType_400_NoContainerCreated()
    {
        var meta = FileServiceHarness.Meta();
        meta.FileType = (FileType)999;

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Upload(meta, FileServiceHarness.Text()));

        Assert.Equal("INVALID_FILE_TYPE", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal(0, _h.Storage.Count);
        Assert.Empty(_h.Storage.Calls);
    }

    [Theory]
    [InlineData("""{"FileType":999}""")]
    [InlineData("""{"FileType":-1}""")]
    public async Task Update_Json_UndefinedFileType_400(string json)
    {
        var file = await _h.UploadTextAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, JsonNode.Parse(json)!.AsObject()));

        Assert.Contains(ex.ErrorCode, new[] { "INVALID_FILE_TYPE", "INVALID_FIELD_VALUE" });
        Assert.Equal(FileType.Text, _h.Reload(file.FileId).FileType);
        Assert.Empty(_h.Storage.Calls);
    }

    [Fact]
    public async Task Update_Entity_UndefinedFileType_400()
    {
        var file = await _h.UploadTextAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, new File { Title = "x", FileType = (FileType)999 }));

        Assert.Equal("INVALID_FILE_TYPE", ex.ErrorCode);
        Assert.Empty(_h.Storage.Calls);
    }

    // A DTO that omits FileType deserialises to Unknown: that must not move the file or delete its thumbnails.
    [Fact]
    public async Task Update_Entity_WithoutFileType_DoesNotChangeTheType()
    {
        var image = await _h.UploadImageAsync();

        await _h.Service.Update(image.FileId, new File { Title = "only title" });

        Assert.Equal(FileType.Image, _h.Reload(image.FileId).FileType);
        Assert.Equal(4, _h.Storage.Count);
        Assert.Empty(_h.Storage.Calls);
    }

    [Fact]
    public async Task Update_Json_UnknownFileType_DoesNotChangeTheType()
    {
        var image = await _h.UploadImageAsync();

        await _h.Service.Update(image.FileId, new JsonObject { ["FileType"] = "Unknown" });

        Assert.Equal(FileType.Image, _h.Reload(image.FileId).FileType);
        Assert.Equal(4, _h.Storage.Count);
    }

    // Upload resets state the client must not choose
    [Fact]
    public async Task Upload_ResetsClientChosenState()
    {
        var meta = FileServiceHarness.Meta();
        meta.IsEnabled = false;
        meta.CreatedDate = new DateTimeOffset(1999, 1, 1, 0, 0, 0, TimeSpan.Zero);
        meta.IsCompressed = false;

        var file = await _h.Service.Upload(meta, FileServiceHarness.Text());

        var row = _h.Reload(file.FileId);
        Assert.True(row.IsEnabled);
        Assert.Equal(_h.Time.GetUtcNow(), row.CreatedDate);
        Assert.True(row.IsCompressed);
    }

    // IsValidToCreate / PreCreate run before anything is stored
    [Fact]
    public async Task Upload_Hooks_RunBeforeContentIsStored()
    {
        _h.Service.RejectCreate = true;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text()));

        Assert.Empty(_h.Storage.Calls);
        Assert.Equal(["IsValidToCreate"], _h.Service.Events);
    }

    // Relocation and thumbnails go through the stream overload with a content type
    [Fact]
    public async Task Relocation_StreamsContent_NoByteArrayBuffering()
    {
        var file = await _h.UploadTextAsync();

        await _h.Service.Update(file.FileId, FileServiceHarness.Meta(FileType.PDF));

        Assert.Contains(_h.Storage.Calls, c => c.StartsWith("Store(stream):PDF/", StringComparison.Ordinal));
        Assert.DoesNotContain(_h.Storage.Calls, c => c.StartsWith("Store(bytes)", StringComparison.Ordinal));
        Assert.Equal("text/plain", _h.Storage.GetContentType(file.FilePath, "PDF"));
    }

    [Fact]
    public async Task RebuiltThumbnails_UseStreamOverload_WithContentType()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(FileType.Unknown), FileServiceHarness.PngUpload());
        _h.Storage.Calls.Clear();

        await _h.Service.Update(file.FileId, FileServiceHarness.Meta(FileType.Image));

        Assert.DoesNotContain(_h.Storage.Calls, c => c.StartsWith("Store(bytes)", StringComparison.Ordinal));
        Assert.Equal("image/png", _h.Storage.GetContentType($"ThumbSmall/{file.FilePath}", "Image"));
    }

    [Fact]
    public async Task GetImage_GeneratedThumbnail_IsCachedThroughTheStreamOverload()
    {
        var file = await _h.UploadImageAsync();
        await _h.Storage.DeleteAsync($"ThumbMedium/{file.FilePath}", "Image");
        _h.Storage.Calls.Clear();

        await using var result = await _h.Service.GetImage(file.FileId, ImageSize.ThumbMedium);

        Assert.DoesNotContain(_h.Storage.Calls, c => c.StartsWith("Store(bytes)", StringComparison.Ordinal));
        Assert.Contains(_h.Storage.Calls, c => c.StartsWith("Store(stream):Image/ThumbMedium/", StringComparison.Ordinal));
        Assert.Equal("image/png", _h.Storage.GetContentType($"ThumbMedium/{file.FilePath}", "Image"));
    }

    [Fact]
    public void Defaults_AreConservative()
    {
        Assert.Equal(25_000_000, new ImageServiceOptions().MaxPixels);
        Assert.Equal(50L * 1024 * 1024, new StorageServiceOptions().MaxFileSizeBytes);
    }
}
