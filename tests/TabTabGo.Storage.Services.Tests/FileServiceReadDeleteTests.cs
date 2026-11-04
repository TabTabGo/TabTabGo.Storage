using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.FileStorage;
using TabTabGo.Storage.FileStorage.Services;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.TestKit;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

public class FileServiceReadTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task GetFile_ReturnsContent_WithContentTypeFromExtension_NotClient()
    {
        var upload = FileUpload.FromBytes(Encoding.UTF8.GetBytes("<script>alert(1)</script>"), "note.txt", "text/html");
        var stored = await _h.Service.Upload(FileServiceHarness.Meta(), upload);
        Assert.Equal("text/html", _h.Reload(stored.FileId).OriginalMediaType);

        await using var result = await _h.Service.GetFile(stored.FileId);

        Assert.Equal("text/plain", result.ContentType);
        Assert.Equal("<script>alert(1)</script>", new StreamReader(result.Stream).ReadToEnd());
    }

    [Fact]
    public async Task GetFile_UnknownExtension_IsOctetStream()
    {
        var stored = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("data.bin2"));

        await using var result = await _h.Service.GetFile(stored.FileId);

        Assert.Equal("application/octet-stream", result.ContentType);
    }

    [Fact]
    public async Task GetFile_Missing_404()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.GetFile(12345));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        Assert.Equal("FILE_NOT_FOUND", ex.ErrorCode);
    }

    [Fact]
    public async Task GetFile_ContentMissingFromStorage_404()
    {
        var file = await _h.UploadTextAsync();
        await _h.Storage.DeleteAsync(file.FilePath, "Text");

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.GetFile(file.FileId));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
    }

    // V-9
    [Fact]
    public async Task GetFile_Cancelled_Throws()
    {
        var file = await _h.UploadTextAsync();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Service.GetFile(file.FileId, cts.Token));
    }

    [Fact]
    public async Task GetFile_PassesTokenToProvider()
    {
        var file = await _h.UploadTextAsync();
        _h.Storage.Tokens.Clear();
        using var cts = new CancellationTokenSource();

        await using var result = await _h.Service.GetFile(file.FileId, cts.Token);

        Assert.Contains(cts.Token, _h.Storage.Tokens);
    }

    [Fact]
    public async Task GetImage_Default_ReturnsOriginal()
    {
        var file = await _h.UploadImageAsync();

        await using var result = await _h.Service.GetImage(file.FileId);

        Assert.Equal("image/png", result.ContentType);
        Assert.Equal((64, 48), TestImages.SizeOf(ReadAll(result.Stream)));
    }

    [Theory]
    [InlineData(ImageSize.ThumbLarge, 180)]
    [InlineData(ImageSize.ThumbMedium, 160)]
    [InlineData(ImageSize.ThumbSmall, 120)]
    public async Task GetImage_Thumbnail_IsServedFromStorage(ImageSize size, int expected)
    {
        var file = await _h.UploadImageAsync();
        _h.Storage.Calls.Clear();

        await using var result = await _h.Service.GetImage(file.FileId, size);

        Assert.Equal((expected, expected), TestImages.SizeOf(ReadAll(result.Stream)));
        Assert.DoesNotContain(_h.Storage.Calls, c => c.StartsWith("Store", StringComparison.Ordinal));
    }

    // V-6
    [Fact]
    public async Task GetImage_MissingThumbnail_GeneratedAndStored()
    {
        var file = await _h.UploadImageAsync();
        await _h.Storage.DeleteAsync($"ThumbMedium/{file.FilePath}", "Image");
        _h.Storage.Calls.Clear();

        await using var result = await _h.Service.GetImage(file.FileId, ImageSize.ThumbMedium);

        Assert.Equal((160, 160), TestImages.SizeOf(ReadAll(result.Stream)));
        Assert.Equal("image/png", result.ContentType);
        Assert.Contains($"Image/ThumbMedium/{file.FilePath}", _h.Storage.Keys());
        Assert.Contains(_h.Storage.Calls, c => c.StartsWith("Store(stream):Image/ThumbMedium/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task GetImage_MissingThumbnail_CacheStoreFails_StillServes()
    {
        var file = await _h.UploadImageAsync();
        await _h.Storage.DeleteAsync($"ThumbSmall/{file.FilePath}", "Image");
        _h.Storage.FailStoreWhen = (_, _) => true;

        // Byte-array stores do not pass through the failure hook, so use a throwing delete-free provider instead.
        await using var result = await _h.Service.GetImage(file.FileId, ImageSize.ThumbSmall);

        Assert.Equal((120, 120), TestImages.SizeOf(ReadAll(result.Stream)));
    }

    [Fact]
    public async Task GetImage_GifThumbnail_IsPngWithPngContentType()
    {
        var file = await _h.UploadImageAsync(upload: FileUpload.FromBytes(TestImages.Gif(16, 12), "g.gif", "image/gif"));

        await using var result = await _h.Service.GetImage(file.FileId, ImageSize.ThumbLarge);

        Assert.Equal("image/png", result.ContentType);
        using var codec = SkiaSharp.SKCodec.Create(new MemoryStream(ReadAll(result.Stream)));
        Assert.Equal(SkiaSharp.SKEncodedImageFormat.Png, codec.EncodedFormat);
    }

    [Fact]
    public async Task GetImage_NotImage_404()
    {
        var file = await _h.UploadTextAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.GetImage(file.FileId, ImageSize.ThumbSmall));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
    }

    [Fact]
    public async Task GetImage_OriginalMissing_404()
    {
        var file = await _h.UploadImageAsync();
        await _h.Storage.DeleteAsync(file.FilePath, "Image");
        await _h.Storage.DeleteAsync($"ThumbSmall/{file.FilePath}", "Image");

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.GetImage(file.FileId, ImageSize.ThumbSmall));
        var ex2 = await Assert.ThrowsAsync<ApiException>(() => _h.Service.GetImage(file.FileId));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ex2.HttpStatusCode);
    }

    [Fact]
    public async Task GetImage_Missing_404() =>
        Assert.Equal(HttpStatusCode.NotFound, (await Assert.ThrowsAsync<ApiException>(() => _h.Service.GetImage(99))).HttpStatusCode);

    [Fact]
    public async Task GetKey_And_GetKeyPredicate()
    {
        var file = await _h.UploadTextAsync();

        Assert.Equal(file.FileId, _h.Service.GetKey(file));
        Assert.True(_h.Service.GetKeyPredicate(file.FileId).Compile()(file));
        Assert.False(_h.Service.GetKeyPredicate(file.FileId + 1).Compile()(file));
    }

    private static byte[] ReadAll(Stream stream)
    {
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}

public class FileServiceListTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private async Task<File> Add(string refType, long? refId, FileType type = FileType.Text)
    {
        var meta = FileServiceHarness.Meta(type);
        meta.ReferenceType = refType;
        meta.ReferenceId = refId;
        return await _h.Service.Upload(meta, type == FileType.Image ? FileServiceHarness.PngUpload() : FileServiceHarness.Text());
    }

    [Fact]
    public async Task ListAsync_NewestFirst_Paged()
    {
        for (var i = 0; i < 5; i++)
        {
            await Add("T", i);
        }

        var page = await _h.Service.ListAsync(new FileListQuery { Page = 2, PageSize = 2 });

        Assert.Equal(5, page.TotalItems);
        Assert.Equal(3, page.TotalPages);
        Assert.Equal(2, page.PageNumber);
        Assert.Equal(2, page.Items.Count);
        Assert.True(page.Items[0].FileId > page.Items[1].FileId);
        Assert.True(page.HasNextPage);
        Assert.True(page.HasPreviousPage);
    }

    [Fact]
    public async Task ListAsync_FiltersByReferenceAndType()
    {
        await Add("Ticket", 1);
        await Add("Ticket", 2);
        await Add("Order", 1);
        await Add("Ticket", 1, FileType.Image);

        var tickets = await _h.Service.ListAsync(new FileListQuery { ReferenceType = "Ticket" });
        var one = await _h.Service.ListAsync(new FileListQuery { ReferenceType = "Ticket", ReferenceId = 1 });
        var images = await _h.Service.ListAsync(new FileListQuery { FileType = FileType.Image });

        Assert.Equal(3, tickets.TotalItems);
        Assert.Equal(2, one.TotalItems);
        Assert.Equal(1, images.TotalItems);
    }

    [Fact]
    public async Task ListAsync_HostScope_IsAndedWithFilters()
    {
        var a = await Add("Ticket", 1);
        await Add("Ticket", 2);
        var allowedId = a.FileId;

        var page = await _h.Service.ListAsync(new FileListQuery(), f => f.FileId == allowedId);
        var none = await _h.Service.ListAsync(new FileListQuery { ReferenceId = 2 }, f => f.FileId == allowedId);

        Assert.Single(page.Items);
        Assert.Equal(0, none.TotalItems);
    }

    [Fact]
    public async Task ListAsync_ExcludesDisabledRows()
    {
        var a = await Add("T", 1);
        await Add("T", 2);
        using (var other = _h.Db.NewContext())
        {
            other.Set<File>().Single(f => f.FileId == a.FileId).IsEnabled = false;
            other.SaveChanges();
        }

        Assert.Equal(1, (await _h.Service.ListAsync(new FileListQuery())).TotalItems);
    }

    [Theory]
    [InlineData(0, 10)]
    [InlineData(-1, 10)]
    [InlineData(1, 0)]
    [InlineData(1, 101)]
    public async Task ListAsync_BadPaging_400(int page, int size)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.ListAsync(new FileListQuery { Page = page, PageSize = size }));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal("INVALID_PAGING", ex.ErrorCode);
    }

    [Fact]
    public async Task ListAsync_MaxPageSize_Accepted() =>
        Assert.NotNull(await _h.Service.ListAsync(new FileListQuery { PageSize = 100 }));

    [Fact]
    public async Task ListAsync_NullQuery_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Service.ListAsync(null!));

    [Fact]
    public async Task ListAsync_EmptyResult_HasOnePageSizeMath()
    {
        var page = await _h.Service.ListAsync(new FileListQuery());

        Assert.Equal(0, page.TotalItems);
        Assert.Empty(page.Items);
        Assert.False(page.HasNextPage);
    }
}

public class FileServiceDeleteTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    // 6.x and Core: Delete(id) is a soft delete and keeps the stored objects.
    [Fact]
    public async Task DeleteById_SoftDeletes_AndKeepsStoredObjects()
    {
        var text = await _h.UploadTextAsync();

        await _h.Service.Delete(text.FileId);

        Assert.Equal(1, _h.Storage.Count);
        Assert.Equal(1, _h.RowCount());
        Assert.False(_h.Reload(text.FileId).IsEnabled);
    }

    [Fact]
    public async Task DeleteById_Image_KeepsOriginalAndThumbnails()
    {
        var image = await _h.UploadImageAsync();

        await _h.Service.Delete(image.FileId);

        Assert.Equal(4, _h.Storage.Count);
        Assert.False(_h.Reload(image.FileId).IsEnabled);
    }

    [Fact]
    public async Task DeleteById_Missing_404()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Delete(404));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
    }

    // V-2
    [Fact]
    public async Task DeleteQuery_RemovesOriginalAndThumbnails()
    {
        var a = await _h.UploadImageAsync();
        var b = await _h.UploadImageAsync("b.png");
        var keep = await _h.UploadTextAsync();
        var idA = a.FileId;
        var idB = b.FileId;

        await _h.Service.Delete(f => f.FileId == idA || f.FileId == idB);

        Assert.Equal([$"Text/{keep.FilePath}"], _h.Storage.Keys());
        Assert.Equal(1, _h.RowCount());
    }

    [Fact]
    public async Task DeleteIds_SoftDeletesOnlyThoseFiles_KeepsObjects()
    {
        var a = await _h.UploadTextAsync();
        var b = await _h.UploadTextAsync();
        var keep = await _h.UploadTextAsync();

        var deleted = (await _h.Service.Delete([a.FileId, b.FileId], CancellationToken.None)).ToList();

        Assert.Equal(2, deleted.Count);
        Assert.Equal(3, _h.Storage.Count);
        Assert.False(_h.Reload(a.FileId).IsEnabled);
        Assert.False(_h.Reload(b.FileId).IsEnabled);
        Assert.True(_h.Reload(keep.FileId).IsEnabled);
    }

    [Fact]
    public async Task Delete_SaveFails_StorageUntouched()
    {
        var image = await _h.UploadImageAsync();
        _h.Uow.FailSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Delete(image.FileId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Delete([image.FileId], CancellationToken.None));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Delete(f => f.FileId == image.FileId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Purge(image.FileId));
        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Purge([image.FileId]));

        Assert.Equal(4, _h.Storage.Count);
    }

    [Fact]
    public async Task Delete_StorageFailure_IsLoggedNotThrown()
    {
        var file = await _h.UploadTextAsync();
        var id = file.FileId;
        _h.Storage.FailDeletes = true;

        await _h.Service.Delete(f => f.FileId == id);
        var other = await _h.UploadTextAsync();
        await _h.Service.Purge(other.FileId);

        Assert.Equal(0, _h.RowCount());
    }

    [Fact]
    public async Task DeleteById_HookOrder()
    {
        var file = await _h.UploadTextAsync();

        await _h.Service.Delete(file.FileId);

        Assert.Equal(["IsValidToDelete", "PreDelete", "PostDelete"], _h.Service.Events);
    }

    [Fact]
    public async Task DeleteBulk_HookOrder()
    {
        var a = await _h.UploadTextAsync();
        var b = await _h.UploadTextAsync();
        _h.Service.Events.Clear();
        await _h.Service.Delete([a.FileId], CancellationToken.None);
        await _h.Service.Delete(f => f.FileId == b.FileId);

        Assert.Equal(["IsValidToDelete", "PreDelete(many)", "PostDelete(many)", "IsValidToDelete", "PreDelete(many)", "PostDelete(many)"],
            _h.Service.Events);
    }

    [Fact]
    public async Task Delete_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Service.Delete((IEnumerable<long>)null!, CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Service.Delete((System.Linq.Expressions.Expression<Func<File, bool>>)null!));
    }
}

// X-3 / X-4: every mutating overload must run the same validation gates as its siblings.
public class FileServiceGateParityTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    private sealed record Op(string Name, string Kind, Func<FileServiceHarness, File, Task> Run);

    private static readonly Op[] Ops =
    [
        new("Update(long,TFile)", "update", (h, f) => h.Service.Update(f.FileId, FileServiceHarness.Meta())),
        new("Update(long,JsonObject)", "update", (h, f) => h.Service.Update(f.FileId, new JsonObject { ["Title"] = "x" })),
        new("Update(long,JsonPatchDocument<TFile>)", "update", (h, f) =>
        {
            var patch = new JsonPatchDocument<File>();
            patch.Replace(x => x.Title, "x");
            return h.Service.Update(f.FileId, patch);
        }),
        new("UpdateChanges(long,TFile)", "update", (h, f) => h.Service.UpdateChanges(f.FileId, FileServiceHarness.Meta(), CancellationToken.None)),
        new("Replace(long,FileUpload)", "update", (h, f) => h.Service.Replace(f.FileId, FileServiceHarness.Text("r.txt", "r"))),
        new("Delete(long)", "delete", (h, f) => h.Service.Delete(f.FileId)),
        new("Delete(IEnumerable<long>)", "delete", (h, f) => h.Service.Delete([f.FileId], CancellationToken.None)),
        new("Delete(Expression)", "delete", (h, f) => h.Service.Delete(x => x.FileId == f.FileId)),
        new("Purge(long)", "delete", (h, f) => h.Service.Purge(f.FileId)),
        new("Purge(IEnumerable<long>)", "delete", (h, f) => h.Service.Purge([f.FileId])),
        new("Upload(TFile,FileUpload)", "create", (h, f) => h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text())),
    ];

    public static IEnumerable<object[]> OpNames() => Ops.Select(o => new object[] { o.Name });

    [Theory]
    [MemberData(nameof(OpNames))]
    public async Task VetoedOperation_IsRejected_AndChangesNothing(string name)
    {
        var op = Ops.Single(o => o.Name == name);
        var file = await _h.UploadTextAsync();
        var keysBefore = _h.Storage.Keys().OrderBy(k => k).ToList();
        var rowsBefore = _h.RowCount();
        _h.Service.RejectCreate = _h.Service.RejectUpdate = _h.Service.RejectDelete = true;

        await Assert.ThrowsAsync<ApiException>(() => op.Run(_h, file));

        Assert.Equal(keysBefore, _h.Storage.Keys().OrderBy(k => k).ToList());
        Assert.Equal(rowsBefore, _h.RowCount());
        Assert.Contains(_h.Service.Events, e => e.StartsWith("IsValidTo" + char.ToUpperInvariant(op.Kind[0]) + op.Kind[1..], StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(OpNames))]
    public async Task AllowedOperation_RunsPreHook(string name)
    {
        var op = Ops.Single(o => o.Name == name);
        var file = await _h.UploadTextAsync();

        await op.Run(_h, file);

        var expectedPre = op.Kind switch { "create" => "PreCreate", "update" => "PreUpdate", _ => "PreDelete" };
        Assert.Contains(_h.Service.Events, e => e.StartsWith(expectedPre, StringComparison.Ordinal));
    }

    [Fact]
    public void Table_CoversEveryPublicMutatingOverload()
    {
        var mutating = typeof(FileService<File>).GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Where(m => m.Name is "Update" or "UpdateChanges" or "Delete" or "Replace" or "Upload" or "Create" or "Purge")
            .Select(Describe)
            .ToHashSet();

        var covered = Ops.Select(o => o.Name).Append("Create(TFile)").ToHashSet();

        Assert.True(covered.SetEquals(mutating),
            $"mutating overloads not covered by the parity table: [{string.Join(", ", mutating.Except(covered))}]; stale rows: [{string.Join(", ", covered.Except(mutating))}]");
    }

    private static string Describe(MethodInfo method)
    {
        string Name(Type t) => t switch
        {
            _ when t == typeof(long) => "long",
            _ when t == typeof(File) => "TFile",
            _ when t == typeof(JsonObject) => "JsonObject",
            _ when t == typeof(FileUpload) => "FileUpload",
            _ when t == typeof(IEnumerable<long>) => "IEnumerable<long>",
            _ when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(JsonPatchDocument<>) => "JsonPatchDocument<TFile>",
            _ when t.IsGenericType && t.GetGenericTypeDefinition() == typeof(System.Linq.Expressions.Expression<>) => "Expression",
            _ => t.Name,
        };

        var args = method.GetParameters().Where(p => p.ParameterType != typeof(CancellationToken)).Select(p => Name(p.ParameterType));
        return $"{method.Name}({string.Join(",", args)})";
    }
}

public class FileServiceFileStorageEndToEndTests : IDisposable
{
    private readonly TempDirectory _temp = new();
    private readonly FileServiceHarnessWithDisk _h;

    public FileServiceFileStorageEndToEndTests() => _h = new FileServiceHarnessWithDisk(_temp.Path);

    public void Dispose()
    {
        _h.Dispose();
        _temp.Dispose();
    }

    // F-2
    [Fact]
    public async Task FileService_UploadImage_FileStorage_StoresThumbnails()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(FileType.Image), FileServiceHarness.PngUpload());

        var imageRoot = Path.Combine(_temp.Path, "store", "Image");
        Assert.True(System.IO.File.Exists(Path.Combine(imageRoot, file.FilePath)));
        foreach (var size in new[] { "ThumbLarge", "ThumbMedium", "ThumbSmall" })
        {
            Assert.True(System.IO.File.Exists(Path.Combine(imageRoot, size, file.FilePath)), size);
        }

        await using var thumb = await _h.Service.GetImage(file.FileId, ImageSize.ThumbMedium);
        Assert.Equal("image/png", thumb.ContentType);
    }

    [Fact]
    public async Task Delete_RemovesFilesFromDisk()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(FileType.Image), FileServiceHarness.PngUpload());

        await _h.Service.Purge(file.FileId);

        Assert.Empty(Directory.GetFiles(Path.Combine(_temp.Path, "store"), "*", SearchOption.AllDirectories));
    }

    private sealed class FileServiceHarnessWithDisk : IDisposable
    {
        private readonly SqliteStorageFixture _db = new();

        public FileServiceHarnessWithDisk(string root)
        {
            var settings = new PathSettings { RootPath = "store" };
            var paths = new PathService(new TestHostEnvironment(root), Options.Create(settings), new FakeLogger<PathService>());
            Service = new FileService<File>(new TestUnitOfWork(_db.Context), new FakeLogger<FileService<File>>(),
                new FileStorageProvider(paths), new ImageService());
        }

        public FileService<File> Service { get; }

        public void Dispose() => _db.Dispose();
    }
}
