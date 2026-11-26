using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.JsonPatch;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.Paths;
using TabTabGo.Storage.TestKit;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

public class FileServiceUploadTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Upload_TextFile_StoresContent_AndInsertsRow()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("Report.TXT", "hello"));

        Assert.True(file.FileId > 0);
        Assert.Matches(@"^20310506/[0-9a-f]{32}\.TXT$", file.FilePath);
        Assert.Equal(5, file.FileSize);
        Assert.Equal(".TXT", file.FileExtension);
        Assert.Equal("Report.TXT", file.OriginalFileName);
        Assert.Equal("text/plain", file.OriginalMediaType);
        await using var stored = await _h.Storage.RetrieveAsync(file.FilePath, "Text");
        Assert.Equal("hello", new StreamReader(stored!).ReadToEnd());
        Assert.Equal(file.FilePath, _h.Reload(file.FileId).FilePath);
    }

    [Fact]
    public async Task Upload_ClientSuppliedStorageFields_AreOverwritten()
    {
        var meta = FileServiceHarness.Meta();
        meta.FileId = 99;
        meta.FilePath = "../../etc/passwd";
        meta.FileSize = 1;
        meta.FileExtension = ".exe";
        meta.OriginalFileName = "evil";
        meta.OriginalMediaType = "text/html";

        var file = await _h.Service.Upload(meta, FileServiceHarness.Text());

        Assert.NotEqual(99, file.FileId);
        Assert.StartsWith("20310506/", file.FilePath);
        Assert.Equal(5, file.FileSize);
        Assert.Equal(".txt", file.FileExtension);
        Assert.Equal("note.txt", file.OriginalFileName);
    }

    // V-7
    [Fact]
    public async Task Upload_PassesStreamToProvider()
    {
        await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());

        Assert.Single(_h.Storage.Calls, c => c.StartsWith("Store(stream):Text/20310506/", StringComparison.Ordinal));
        Assert.DoesNotContain(_h.Storage.Calls, c => c.StartsWith("Store(bytes", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Upload_ContentTypeStoredFromExtension()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("a.pdf"));

        Assert.Equal("application/pdf", _h.Storage.GetContentType(file.FilePath, "Text"));
    }

    [Fact]
    public async Task Upload_DefaultsRequiredStrings_WhenClientOmitsThem()
    {
        var meta = FileServiceHarness.Meta();
        meta.Title = null!;
        meta.Comment = null!;
        meta.ReferenceType = null!;

        var file = await _h.Service.Upload(meta, FileServiceHarness.Text("named.txt"));

        Assert.Equal("named.txt", file.Title);
        Assert.Equal(string.Empty, file.Comment);
        Assert.Equal(string.Empty, file.ReferenceType);
    }

    [Fact]
    public async Task Upload_Image_StoresOriginalAndThumbnails()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(FileType.Image), FileServiceHarness.PngUpload());

        var keys = _h.Storage.Keys();
        Assert.Contains($"Image/{file.FilePath}", keys);
        foreach (var size in new[] { "ThumbLarge", "ThumbMedium", "ThumbSmall" })
        {
            Assert.Contains($"Image/{size}/{file.FilePath}", keys);
        }

        Assert.Equal(4, _h.Storage.Count);
    }

    // V-8
    [Fact]
    public void ThumbnailPath_UsesForwardSlash()
    {
        Assert.Equal("ThumbLarge/20310506/x.png", FileService<File>.ThumbnailPath(ImageSize.ThumbLarge, "20310506\\x.png"));
        Assert.Equal("ThumbSmall/a/b.jpg", FileService<File>.ThumbnailPath(ImageSize.ThumbSmall, "a/b.jpg"));
    }

    [Theory]
    [InlineData("a/b.gif", "ThumbMedium/a/b.png")]
    [InlineData("a/b.BMP", "ThumbMedium/a/b.png")]
    [InlineData("a/b.ico", "ThumbMedium/a/b.png")]
    [InlineData("a/b", "ThumbMedium/a/b")]
    public void ThumbnailPath_NonEncodableSources_BecomePng(string path, string expected) =>
        Assert.Equal(expected, FileService<File>.ThumbnailPath(ImageSize.ThumbMedium, path));

    [Fact]
    public void ThumbnailPath_Traversal_Throws() =>
        Assert.Throws<ArgumentException>(() => FileService<File>.ThumbnailPath(ImageSize.ThumbSmall, "../x.png"));

    [Fact]
    public async Task Upload_GifImage_ThumbnailsArePng()
    {
        var gif = FileUpload.FromBytes(TestImages.Gif(16, 12), "anim.gif", "image/gif");

        var file = await _h.Service.Upload(FileServiceHarness.Meta(FileType.Image), gif);

        Assert.Contains($"Image/ThumbLarge/{file.FilePath[..^4]}.png", _h.Storage.Keys());
        Assert.Equal("image/png", _h.Storage.GetContentType($"ThumbLarge/{file.FilePath[..^4]}.png", "Image"));
    }

    // V-5
    [Theory]
    [InlineData("page.html")]
    [InlineData("page.HTM")]
    [InlineData("vector.svg")]
    [InlineData("script.js")]
    [InlineData("module.mjs")]
    [InlineData("data.xml")]
    [InlineData("page.xhtml")]
    [InlineData("page.xht")]
    [InlineData("style.xsl")]
    [InlineData("style.xslt")]
    [InlineData("tool.exe")]
    [InlineData("lib.dll")]
    [InlineData("run.sh")]
    [InlineData("run.bat")]
    [InlineData("shell.php")]
    [InlineData("page.aspx")]
    public async Task Upload_DeniedExtension_400(string name)
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text(name)));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal("FILE_EXTENSION_DENIED", ex.ErrorCode);
        Assert.Equal(0, _h.Storage.Count);
        Assert.Equal(0, _h.RowCount());
    }

    [Fact]
    public async Task Upload_ExtensionTooLong_400_NothingStored()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() =>
            _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("a.verylongext")));

        Assert.Equal("FILE_EXTENSION_INVALID", ex.ErrorCode);
        Assert.Equal(0, _h.Storage.Count);
    }

    [Fact]
    public async Task Upload_ExtensionWithOddCharacters_400() =>
        Assert.Equal("FILE_EXTENSION_INVALID", (await Assert.ThrowsAsync<ApiException>(() =>
            _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("a.p ng")))).ErrorCode);

    [Fact]
    public async Task Upload_ExtensionOfExactlyTenCharacters_Accepted()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("a.123456789"));

        Assert.Equal(".123456789", file.FileExtension);
    }

    [Fact]
    public async Task Upload_AllowedExtensions_RestrictsToList()
    {
        using var h = new FileServiceHarness(o => o.AllowedExtensions.Add(".pdf"));

        await h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("ok.pdf"));
        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("no.txt")));

        Assert.Equal("FILE_EXTENSION_NOT_ALLOWED", ex.ErrorCode);
    }

    // V-5 (name)
    [Theory]
    [InlineData("../../etc/passwd.txt", "passwd.txt")]
    [InlineData("C:\\Users\\x\\doc.txt", "doc.txt")]
    [InlineData("a/b/c.txt", "c.txt")]
    [InlineData("bad\r\nname.txt", "badname.txt")]
    [InlineData("   spaced.txt  ", "spaced.txt")]
    public async Task Upload_FileNameWithPath_Sanitised(string clientName, string expected)
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text(clientName));

        Assert.Equal(expected, file.OriginalFileName);
        Assert.True(StoragePath.IsSafe(file.FilePath));
    }

    [Fact]
    public async Task Upload_FileName_BidiControlCharactersAreStripped()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text("photo\u202Egnp.txt"));

        Assert.Equal("photognp.txt", file.OriginalFileName);
        Assert.DoesNotContain(file.OriginalFileName, c => c is >= '\u202A' and <= '\u202E' or >= '\u2066' and <= '\u2069');
    }

    [Fact]
    public async Task Upload_EmptyName_BecomesFile()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileUpload.FromBytes([1], "///", null));

        Assert.Equal("file", file.OriginalFileName);
        Assert.Equal(string.Empty, file.OriginalMediaType);
    }

    [Fact]
    public async Task Upload_VeryLongName_IsTruncatedKeepingExtension()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text(new string('n', 700) + ".txt"));

        Assert.Equal(500, file.OriginalFileName.Length);
        Assert.EndsWith(".txt", file.OriginalFileName);
    }

    [Fact]
    public async Task Upload_LongMediaType_IsTruncated()
    {
        var upload = FileUpload.FromBytes([1], "a.txt", new string('m', 900));

        var file = await _h.Service.Upload(FileServiceHarness.Meta(), upload);

        Assert.Equal(500, file.OriginalMediaType.Length);
    }

    // V-5 (size)
    [Fact]
    public async Task Upload_TooLarge_413()
    {
        using var h = new FileServiceHarness(o => o.MaxFileSizeBytes = 4);

        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text(content: "12345")));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.HttpStatusCode);
        Assert.Equal(0, h.Storage.Count);
    }

    [Fact]
    public async Task Upload_TooLarge_UnknownLength_StopsWhileStreaming()
    {
        using var h = new FileServiceHarness(o => o.MaxFileSizeBytes = 4);
        var upload = FileUpload.FromFactory(() => new MemoryStream(new byte[100]), "a.bin");

        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Service.Upload(FileServiceHarness.Meta(), upload));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.HttpStatusCode);
        Assert.Equal(0, h.Storage.Count);
        Assert.Equal(0, h.RowCount());
    }

    [Fact]
    public async Task Upload_ExactlyAtLimit_Accepted()
    {
        using var h = new FileServiceHarness(o => o.MaxFileSizeBytes = 5);

        var file = await h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());

        Assert.Equal(5, file.FileSize);
    }

    [Fact]
    public async Task Upload_UnknownLength_RecordsCountedSize()
    {
        var upload = FileUpload.FromFactory(() => new MemoryStream(new byte[37]), "a.bin");

        var file = await _h.Service.Upload(FileServiceHarness.Meta(), upload);

        Assert.Equal(37, file.FileSize);
    }

    // V-2 / A-2
    [Fact]
    public async Task Upload_InsertFails_StoredObjectsRemoved()
    {
        var meta = FileServiceHarness.Meta(FileType.Image);
        meta.CreatedBy = null!; // NOT NULL column: the insert fails after the objects were stored

        await Assert.ThrowsAnyAsync<Exception>(() => _h.Service.Upload(meta, FileServiceHarness.PngUpload()));

        Assert.Equal(0, _h.Storage.Count);
        Assert.Equal(0, _h.RowCount());
        Assert.Equal(4, _h.Storage.Calls.Count(c => c.StartsWith("Delete:", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task Upload_SaveFails_StoredObjectsRemoved()
    {
        _h.Uow.FailSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text()));

        Assert.Equal(0, _h.Storage.Count);
    }

    [Fact]
    public async Task Upload_StoreFails_NoRowInserted()
    {
        _h.Storage.FailStoreWhen = (_, _) => true;

        var ex = await Assert.ThrowsAsync<StorageException>(() => _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text()));

        Assert.Equal("Store", ex.Operation);
        Assert.Equal(0, _h.RowCount());
    }

    [Fact]
    public async Task Upload_ThumbnailStoreFails_CleansUpOriginal()
    {
        _h.Storage.FailStoreWhen = (_, path) => path.StartsWith("ThumbSmall/", StringComparison.Ordinal);

        await Assert.ThrowsAsync<StorageException>(() => _h.Service.Upload(FileServiceHarness.Meta(FileType.Image), FileServiceHarness.PngUpload()));

        Assert.Equal(0, _h.Storage.Count);
        Assert.Equal(0, _h.RowCount());
    }

    [Fact]
    public async Task Upload_CleanupFailure_DoesNotMaskTheOriginalError()
    {
        _h.Uow.FailSave = true;
        _h.Storage.FailDeletes = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text()));
    }

    [Fact]
    public async Task Upload_Image_NotAnImage_400_NothingStored()
    {
        var upload = FileUpload.FromBytes(TestImages.Corrupt(), "bad.png", "image/png");

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Upload(FileServiceHarness.Meta(FileType.Image), upload));

        Assert.Equal("IMAGE_NOT_DECODABLE", ex.ErrorCode);
        Assert.Equal(0, _h.Storage.Count);
        Assert.Equal(0, _h.RowCount());
    }

    [Fact]
    public async Task Upload_Image_PixelBomb_Rejected()
    {
        var upload = FileUpload.FromBytes(TestImages.PngDeclaring(40_000, 40_000), "bomb.png", "image/png");

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Upload(FileServiceHarness.Meta(FileType.Image), upload));

        Assert.Equal("IMAGE_TOO_LARGE", ex.ErrorCode);
    }

    [Fact]
    public async Task Upload_Image_UnsupportedFormatExtension_400()
    {
        var upload = FileUpload.FromBytes(TestImages.Png(8, 8), "photo.tiff", "image/tiff");

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Upload(FileServiceHarness.Meta(FileType.Image), upload));

        Assert.Equal("IMAGE_FORMAT_NOT_SUPPORTED", ex.ErrorCode);
        Assert.Equal(0, _h.Storage.Count);
    }

    // SB5
    [Fact]
    public async Task Create_WithoutContent_400()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Create(FileServiceHarness.Meta()));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal("FILE_CONTENT_REQUIRED", ex.ErrorCode);
    }

    [Fact]
    public async Task Upload_HookOrder_IsValid_PreCreate_Insert_PostCreate()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());

        Assert.Equal(["IsValidToCreate", "PreCreate", "PostCreate"], _h.Service.Events);
        Assert.True(file.FileId > 0);
    }

    [Fact]
    public async Task Upload_ValidationVeto_400_AndNothingPersisted()
    {
        _h.Service.RejectCreate = true;

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text()));

        Assert.Equal("FILE_INVALID", ex.ErrorCode);
        Assert.Equal(0, _h.Storage.Count);
        Assert.Equal(0, _h.RowCount());
    }

    [Fact]
    public async Task Upload_Cancelled_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text(), cts.Token));

        Assert.Equal(0, _h.Storage.Count);
    }

    [Fact]
    public async Task Upload_PassesCancellationTokenToProvider()
    {
        using var cts = new CancellationTokenSource();

        await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text(), cts.Token);

        Assert.Contains(cts.Token, _h.Storage.Tokens);
    }

    [Fact]
    public async Task Upload_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Service.Upload(null!, FileServiceHarness.Text()));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Service.Upload(FileServiceHarness.Meta(), null!));
    }

    [Fact]
    public async Task Upload_StreamBackedContent_LeavesCallerStreamOpen()
    {
        var source = new MemoryStream(Encoding.UTF8.GetBytes("abc"));

        await _h.Service.Upload(FileServiceHarness.Meta(), FileUpload.FromStream(source, "s.txt"));

        Assert.True(source.CanRead);
    }

    [Fact]
    public async Task Upload_TwoFilesSameName_GetDistinctPaths()
    {
        var a = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());
        var b = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());

        Assert.NotEqual(a.FilePath, b.FilePath);
    }
}

public class FileServiceReplaceAndUpdateTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    // V-1
    [Fact]
    public async Task Update_ClientFilePath_Ignored()
    {
        var file = await _h.UploadTextAsync();
        var original = file.FilePath;
        var update = FileServiceHarness.Meta();
        update.Title = "new title";
        update.FilePath = "../../escape";
        update.FileSize = 1;
        update.FileExtension = ".exe";
        update.OriginalFileName = "evil.exe";
        update.OriginalMediaType = "text/html";

        await _h.Service.Update(file.FileId, update);

        var stored = _h.Reload(file.FileId);
        Assert.Equal("new title", stored.Title);
        Assert.Equal(original, stored.FilePath);
        Assert.Equal(5, stored.FileSize);
        Assert.Equal(".txt", stored.FileExtension);
        Assert.Equal("note.txt", stored.OriginalFileName);
        Assert.Equal("text/plain", stored.OriginalMediaType);
    }

    [Fact]
    public async Task Update_Entity_CopiesAllowListedFields()
    {
        using var h = new FileServiceHarness(o => o.AllowMetadataExtraProperties = true);
        var file = await h.UploadTextAsync();
        var update = new File { Title = "T2", Comment = "C2", ReferenceType = "Order", ReferenceId = 55, Duration = 12, FileType = FileType.Text };
        update.ExtraProperties["k"] = "v";

        await h.Service.Update(file.FileId, update);

        var stored = h.Reload(file.FileId);
        Assert.Equal("T2", stored.Title);
        Assert.Equal("C2", stored.Comment);
        Assert.Equal("Order", stored.ReferenceType);
        Assert.Equal(55, stored.ReferenceId);
        Assert.Equal(12, stored.Duration);
        Assert.Equal("v", stored.ExtraProperties["k"].ToString());
    }

    [Fact]
    public async Task Update_Entity_NullStrings_KeepCurrentValues()
    {
        var file = await _h.UploadTextAsync();
        var update = new File { Title = null!, Comment = null!, ReferenceType = null!, FileType = FileType.Text };

        await _h.Service.Update(file.FileId, update);

        var stored = _h.Reload(file.FileId);
        Assert.Equal("title", stored.Title);
        Assert.Equal("comment", stored.Comment);
        Assert.Equal("Ticket", stored.ReferenceType);
    }

    [Fact]
    public async Task Update_Json_FilePathAndSize_Ignored()
    {
        var file = await _h.UploadTextAsync();
        var json = JsonNode.Parse("""{"FilePath":"../../x","FileSize":999,"FileId":12345,"FileExtension":".exe","OriginalFileName":"x","OriginalMediaType":"text/html","IsEnabled":false,"CreatedBy":"attacker","Title":"ok"}""")!.AsObject();

        await _h.Service.Update(file.FileId, json);

        var stored = _h.Reload(file.FileId);
        Assert.Equal("ok", stored.Title);
        Assert.Equal(file.FilePath, stored.FilePath);
        Assert.Equal(5, stored.FileSize);
        Assert.Equal(file.FileId, stored.FileId);
        Assert.Equal(".txt", stored.FileExtension);
        Assert.True(stored.IsEnabled);
        Assert.Equal("tester", stored.CreatedBy);
    }

    [Fact]
    public async Task Update_Json_OnlyProvidedFieldsChange()
    {
        var file = await _h.UploadTextAsync();

        await _h.Service.Update(file.FileId, JsonNode.Parse("""{"comment":"only this"}""")!.AsObject());

        var stored = _h.Reload(file.FileId);
        Assert.Equal("only this", stored.Comment);
        Assert.Equal("title", stored.Title);
        Assert.Equal("Ticket", stored.ReferenceType);
        Assert.Equal(7, stored.ReferenceId);
        Assert.Equal(FileType.Text, stored.FileType);
    }

    [Fact]
    public async Task Update_Json_ReferenceFieldsAndNulls()
    {
        var file = await _h.UploadTextAsync();

        await _h.Service.Update(file.FileId, JsonNode.Parse("""{"ReferenceId":null,"Duration":30,"ReferenceType":"Order","Comment":null}""")!.AsObject());

        var stored = _h.Reload(file.FileId);
        Assert.Null(stored.ReferenceId);
        Assert.Equal(30, stored.Duration);
        Assert.Equal("Order", stored.ReferenceType);
        Assert.Equal(string.Empty, stored.Comment);
    }

    [Fact]
    public async Task Update_Json_NullReferenceType_BecomesEmpty()
    {
        var file = await _h.UploadTextAsync();

        await _h.Service.Update(file.FileId, JsonNode.Parse("""{"ReferenceType":null}""")!.AsObject());

        Assert.Equal(string.Empty, _h.Reload(file.FileId).ReferenceType);
    }

    [Fact]
    public async Task Update_ExtraProperties_Merged()
    {
        using var h = new FileServiceHarness(o => o.AllowMetadataExtraProperties = true);
        var file = await h.UploadTextAsync();
        await h.Service.Update(file.FileId, JsonNode.Parse("""{"ExtraProperties":{"a":1,"b":"x"}}""")!.AsObject());

        await h.Service.Update(file.FileId, JsonNode.Parse("""{"ExtraProperties":{"b":"y","c":true,"d":null}}""")!.AsObject());

        var extra = h.Reload(file.FileId).ExtraProperties;
        Assert.Equal("1", extra["a"].ToString());
        Assert.Equal("y", extra["b"].ToString());
        Assert.Equal("True", extra["c"].ToString(), ignoreCase: true);
        Assert.True(extra.ContainsKey("d"));
    }

    [Fact]
    public async Task Update_Json_InvalidFieldValue_400()
    {
        var file = await _h.UploadTextAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, JsonNode.Parse("""{"Duration":"not a number"}""")!.AsObject()));

        Assert.Equal("INVALID_FIELD_VALUE", ex.ErrorCode);
        var ex2 = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, JsonNode.Parse("""{"Title":{"x":1}}""")!.AsObject()));
        Assert.Equal("INVALID_FIELD_VALUE", ex2.ErrorCode);
        var ex3 = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, JsonNode.Parse("""{"FileType":"Nonsense"}""")!.AsObject()));
        Assert.Equal("INVALID_FIELD_VALUE", ex3.ErrorCode);
    }

    // V-3
    [Fact]
    public async Task Update_Missing_404()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(404, FileServiceHarness.Meta()));
        var ex2 = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(404, new JsonObject()));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
        Assert.Equal("FILE_NOT_FOUND", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.NotFound, ex2.HttpStatusCode);
    }

    [Fact]
    public async Task Update_HookOrder_IsValid_PreUpdate_PostUpdate()
    {
        var file = await _h.UploadTextAsync();

        await _h.Service.Update(file.FileId, FileServiceHarness.Meta());
        await _h.Service.Update(file.FileId, new JsonObject());

        Assert.Equal(["IsValidToUpdate", "IsValidToUpdate", "PreUpdate", "PostUpdate", "IsValidToUpdate", "IsValidToUpdate", "PreUpdate(json)", "PostUpdate"], _h.Service.Events);
    }

    [Fact]
    public async Task Update_JsonPatch_AllowListedField_Applies()
    {
        var file = await _h.UploadTextAsync();
        var patch = new JsonPatchDocument<File>();
        patch.Replace(f => f.Title, "patched");

        await _h.Service.Update(file.FileId, patch);

        Assert.Equal("patched", _h.Reload(file.FileId).Title);
    }

    [Theory]
    [InlineData("/FilePath")]
    [InlineData("/filesize")]
    [InlineData("/FileId")]
    [InlineData("/IsEnabled")]
    [InlineData("/CreatedBy")]
    [InlineData("/FileType")]
    public async Task Update_JsonPatch_StorageOwnedField_Rejected(string path)
    {
        var file = await _h.UploadTextAsync();
        var patch = new JsonPatchDocument<File>();
        patch.Operations.Add(new Microsoft.AspNetCore.JsonPatch.Operations.Operation<File>("replace", path, null, "x"));

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, patch));

        Assert.Equal("FIELD_NOT_UPDATABLE", ex.ErrorCode);
        Assert.Equal(file.FilePath, _h.Reload(file.FileId).FilePath);
    }

    [Fact]
    public async Task Update_JsonPatch_RemoveOperation_Rejected()
    {
        var file = await _h.UploadTextAsync();
        var patch = new JsonPatchDocument<File>();
        patch.Remove(f => f.Title);

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, patch));
    }

    // V-4: changing the type moves the object; the old objects are removed from the container of the OLD type
    [Fact]
    public async Task Replace_TypeChanged_DeletesFromOldContainer()
    {
        var file = await _h.UploadImageAsync();
        Assert.Equal(4, _h.Storage.Count);

        await _h.Service.Update(file.FileId, JsonNode.Parse("""{"FileType":"PDF"}""")!.AsObject());

        var keys = _h.Storage.Keys();
        Assert.Single(keys);
        Assert.Contains($"PDF/{file.FilePath}", keys);
        Assert.Equal(FileType.PDF, _h.Reload(file.FileId).FileType);
        Assert.DoesNotContain(keys, k => k.StartsWith("Image/", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Update_FileType_NonImageToImage_BuildsThumbnails()
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(FileType.Unknown), FileServiceHarness.PngUpload());
        Assert.Equal(1, _h.Storage.Count);

        await _h.Service.Update(file.FileId, FileServiceHarness.Meta(FileType.Image));

        Assert.Equal(4, _h.Storage.Count);
        Assert.Contains($"Image/ThumbSmall/{file.FilePath}", _h.Storage.Keys());
        Assert.DoesNotContain($"Unknown/{file.FilePath}", _h.Storage.Keys());
    }

    [Fact]
    public async Task Update_FileType_ToImage_ForNonImageContent_400_AndNothingChanges()
    {
        var file = await _h.UploadTextAsync();

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, FileServiceHarness.Meta(FileType.Image)));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal(FileType.Text, _h.Reload(file.FileId).FileType);
        Assert.Equal(1, _h.Storage.Count);
        Assert.Contains($"Text/{file.FilePath}", _h.Storage.Keys());
    }

    [Fact]
    public async Task Update_FileType_ContentMissing_404()
    {
        var file = await _h.UploadTextAsync();
        await _h.Storage.DeleteAsync(file.FilePath, "Text");

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, FileServiceHarness.Meta(FileType.PDF)));

        Assert.Equal(HttpStatusCode.NotFound, ex.HttpStatusCode);
    }

    [Fact]
    public async Task Update_FileType_SaveFails_RevertsAndRemovesCopies()
    {
        var file = await _h.UploadTextAsync();
        _h.Uow.FailSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Update(file.FileId, FileServiceHarness.Meta(FileType.PDF)));

        Assert.Equal([$"Text/{file.FilePath}"], _h.Storage.Keys());
    }

    [Fact]
    public async Task Replace_StoresNewContent_AndRemovesOldObjects()
    {
        var file = await _h.UploadImageAsync();
        var oldPath = file.FilePath;

        var replaced = await _h.Service.Replace(file.FileId, FileServiceHarness.PngUpload("new.png", 20, 20));

        Assert.NotEqual(oldPath, replaced.FilePath);
        Assert.Equal("new.png", replaced.OriginalFileName);
        var keys = _h.Storage.Keys();
        Assert.Equal(4, keys.Count);
        Assert.DoesNotContain(keys, k => k.EndsWith(oldPath, StringComparison.Ordinal));
        Assert.Contains($"Image/{replaced.FilePath}", keys);
        Assert.Equal(replaced.FilePath, _h.Reload(file.FileId).FilePath);
    }

    [Fact]
    public async Task GetImage_UndefinedSize_400_BeforeAnyStorageAccess()
    {
        var file = await _h.UploadImageAsync();
        _h.Storage.Tokens.Clear();

        var ex = await Assert.ThrowsAsync<ApiException>(() => _h.Service.GetImage(file.FileId, (ImageSize)99));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal("IMAGE_SIZE_NOT_SUPPORTED", ex.ErrorCode);
        Assert.Empty(_h.Storage.Tokens);
    }

    [Fact]
    public async Task Replace_Missing_404() =>
        Assert.Equal(HttpStatusCode.NotFound,
            (await Assert.ThrowsAsync<ApiException>(() => _h.Service.Replace(404, FileServiceHarness.Text()))).HttpStatusCode);

    [Fact]
    public async Task Replace_DeniedExtension_400_AndNothingChanges()
    {
        var file = await _h.UploadTextAsync();

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Replace(file.FileId, FileServiceHarness.Text("x.html")));

        Assert.Equal(file.FilePath, _h.Reload(file.FileId).FilePath);
        Assert.Equal(1, _h.Storage.Count);
    }

    [Fact]
    public async Task Replace_SaveFails_KeepsOldAndRemovesNew()
    {
        var file = await _h.UploadTextAsync();
        var oldPath = file.FilePath;
        _h.Uow.FailSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Replace(file.FileId, FileServiceHarness.Text("n.txt", "new")));

        Assert.Equal([$"Text/{oldPath}"], _h.Storage.Keys());
        Assert.Equal(oldPath, file.FilePath);
        Assert.Equal("note.txt", file.OriginalFileName);
    }

    [Fact]
    public async Task Replace_SaveFails_LegacyNullFileSize_StaysNull()
    {
        var file = await _h.UploadTextAsync();
        file.FileSize = null;
        await _h.Db.Context.SaveChangesAsync();
        _h.Uow.FailSave = true;

        await Assert.ThrowsAsync<InvalidOperationException>(() => _h.Service.Replace(file.FileId, FileServiceHarness.Text("n.txt", "new")));

        Assert.Null(file.FileSize);
    }

    [Theory]
    [InlineData("ar-SA")]
    [InlineData("th-TH")]
    public async Task Upload_DateFolder_IsCultureInvariant(string culture)
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        try
        {
            var file = await _h.UploadTextAsync();

            Assert.StartsWith("20310506/", file.FilePath);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public async Task Replace_OldDeleteFails_IsLoggedNotThrown()
    {
        var file = await _h.UploadTextAsync();
        var oldPath = file.FilePath;
        _h.Storage.FailDeletes = true;

        var replaced = await _h.Service.Replace(file.FileId, FileServiceHarness.Text("n.txt", "new"));

        Assert.NotEqual(oldPath, replaced.FilePath);
    }

    [Fact]
    public async Task Replace_RunsUpdateHooks()
    {
        var file = await _h.UploadTextAsync();

        await _h.Service.Replace(file.FileId, FileServiceHarness.Text("n.txt", "new"));

        Assert.Equal(["IsValidToUpdate", "IsValidToUpdate", "PreUpdate", "PostUpdate"], _h.Service.Events);
    }

    [Fact]
    public async Task Replace_NullContent_Throws() =>
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Service.Replace(1, null!));

    [Fact]
    public async Task Update_NullArguments_Throw()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Service.Update(1, (File)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Service.Update(1, (JsonObject)null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Service.Update(1, (JsonPatchDocument<File>)null!));
    }
}
