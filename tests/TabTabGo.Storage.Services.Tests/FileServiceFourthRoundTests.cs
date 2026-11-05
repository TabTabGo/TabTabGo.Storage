using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.JsonPatch;
using Microsoft.EntityFrameworkCore;
using TabTabGo.Core.Exceptions;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.TestKit;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

// A: ExtraProperties follows AllowMetadataExtraProperties on updates too, and is bounded when it is allowed.
public class FileServiceExtraPropertiesOptionTests
{
    private static JsonObject Extra(string key, object value) => new() { ["ExtraProperties"] = new JsonObject { [key] = JsonValue.Create(value) } };

    [Fact]
    public async Task OptionOff_Update_Entity_IgnoresExtraProperties()
    {
        using var h = new FileServiceHarness();
        var file = await h.UploadTextAsync();
        var dto = new File { Title = "x", FileType = FileType.Text };
        dto.ExtraProperties["role"] = "admin";

        await h.Service.Update(file.FileId, dto);

        Assert.Empty(h.Reload(file.FileId).ExtraProperties);
        Assert.Equal("x", h.Reload(file.FileId).Title);
    }

    [Fact]
    public async Task OptionOff_Update_Json_IgnoresExtraProperties()
    {
        using var h = new FileServiceHarness();
        var file = await h.UploadTextAsync();

        await h.Service.Update(file.FileId, Extra("role", "admin"));

        Assert.Empty(h.Reload(file.FileId).ExtraProperties);
    }

    [Fact]
    public async Task OptionOff_Patch_OnExtraProperties_Is400_FieldNotUpdatable()
    {
        using var h = new FileServiceHarness();
        var file = await h.UploadTextAsync();
        var patch = new JsonPatchDocument<File>();
        patch.Add(f => f.ExtraProperties["role"], "admin");

        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Service.Update(file.FileId, patch));

        Assert.Equal("FIELD_NOT_UPDATABLE", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Empty(h.Reload(file.FileId).ExtraProperties);
    }

    [Fact]
    public async Task OptionOff_UpdateChanges_IgnoresExtraProperties_AndATrackedEditIsRestored()
    {
        using var h = new FileServiceHarness();
        var file = await h.UploadTextAsync();
        var tracked = h.Db.Context.Set<File>().Local.Single(f => f.FileId == file.FileId);
        tracked.ExtraProperties["smuggled"] = "yes";
        var dto = new File { Title = "x", FileType = FileType.Text };
        dto.ExtraProperties["role"] = "admin";

        await h.Service.UpdateChanges(file.FileId, dto, CancellationToken.None);

        Assert.Empty(h.Reload(file.FileId).ExtraProperties);
    }

    [Fact]
    public async Task OptionOn_Patch_OnExtraProperties_Works()
    {
        using var h = new FileServiceHarness(o => o.AllowMetadataExtraProperties = true);
        var file = await h.UploadTextAsync();
        var patch = new JsonPatchDocument<File>();
        patch.Add(f => f.ExtraProperties["source"], "scanner");

        await h.Service.Update(file.FileId, patch);

        Assert.Equal("scanner", h.Reload(file.FileId).ExtraProperties["source"].ToString());
    }

    [Fact]
    public void Limits_HaveSafeDefaults()
    {
        var options = new StorageServiceOptions();

        Assert.Equal(32, options.MaxExtraPropertyKeys);
        Assert.Equal(16 * 1024, options.MaxExtraPropertiesBytes);
    }

    [Fact]
    public async Task Upload_TooManyKeys_400()
    {
        using var h = new FileServiceHarness(o =>
        {
            o.AllowMetadataExtraProperties = true;
            o.MaxExtraPropertyKeys = 3;
        });
        var meta = FileServiceHarness.Meta();
        for (var i = 0; i < 4; i++)
        {
            meta.ExtraProperties["k" + i] = i;
        }

        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Service.Upload(meta, FileServiceHarness.Text()));

        Assert.Equal("EXTRA_PROPERTIES_TOO_LARGE", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal(0, h.Storage.Count);
        Assert.Empty(h.Storage.Calls);
    }

    [Fact]
    public async Task Upload_TooLarge_400()
    {
        using var h = new FileServiceHarness(o =>
        {
            o.AllowMetadataExtraProperties = true;
            o.MaxExtraPropertiesBytes = 100;
        });
        var meta = FileServiceHarness.Meta();
        meta.ExtraProperties["blob"] = new string('x', 500);

        var ex = await Assert.ThrowsAsync<ApiException>(() => h.Service.Upload(meta, FileServiceHarness.Text()));

        Assert.Equal("EXTRA_PROPERTIES_TOO_LARGE", ex.ErrorCode);
    }

    [Fact]
    public async Task Upload_AtTheLimit_IsAccepted()
    {
        using var h = new FileServiceHarness(o =>
        {
            o.AllowMetadataExtraProperties = true;
            o.MaxExtraPropertyKeys = 2;
        });
        var meta = FileServiceHarness.Meta();
        meta.ExtraProperties["a"] = 1;
        meta.ExtraProperties["b"] = 2;

        var file = await h.Service.Upload(meta, FileServiceHarness.Text());

        Assert.Equal(2, h.Reload(file.FileId).ExtraProperties.Count);
    }

    [Fact]
    public async Task Update_Entity_Json_Patch_RejectBagsOverTheLimit_AndChangeNothing()
    {
        using var h = new FileServiceHarness(o =>
        {
            o.AllowMetadataExtraProperties = true;
            o.MaxExtraPropertyKeys = 2;
            o.MaxExtraPropertiesBytes = 200;
        });
        var file = await h.UploadTextAsync();
        await h.Service.Update(file.FileId, Extra("a", 1));
        var tooManyKeys = new File { Title = "t", FileType = FileType.Text };
        tooManyKeys.ExtraProperties["b"] = 2;
        tooManyKeys.ExtraProperties["c"] = 3;
        var patch = new JsonPatchDocument<File>();
        patch.Add(f => f.ExtraProperties["z"], "zz");
        patch.Add(f => f.ExtraProperties["y"], "yy");

        foreach (var call in new Func<Task>[]
                 {
                     () => h.Service.Update(file.FileId, tooManyKeys),
                     () => h.Service.Update(file.FileId, new JsonObject { ["ExtraProperties"] = new JsonObject { ["b"] = 2, ["c"] = 3 } }),
                     () => h.Service.Update(file.FileId, Extra("big", new string('x', 500))),
                     () => h.Service.Update(file.FileId, patch),
                     () => h.Service.UpdateChanges(file.FileId, tooManyKeys, CancellationToken.None),
                 })
        {
            var ex = await Assert.ThrowsAsync<ApiException>(call);
            Assert.Equal("EXTRA_PROPERTIES_TOO_LARGE", ex.ErrorCode);
        }

        var extra = h.Reload(file.FileId).ExtraProperties;
        Assert.Single(extra);
        Assert.Equal("1", extra["a"].ToString());
    }
}

// D: UpdatedDate is set by the service on every successful update; UpdatedBy comes from PreUpdate or the host's interceptor.
public class FileServiceAuditTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task UpdatedDate_IsSetFromTheTimeProvider_OnUpdateReplaceAndSoftDelete()
    {
        var file = await _h.UploadTextAsync();

        _h.Time.Advance(TimeSpan.FromHours(1));
        await _h.Service.Update(file.FileId, new File { Title = "a", FileType = FileType.Text });
        Assert.Equal(_h.Time.GetUtcNow(), _h.Reload(file.FileId).UpdatedDate);

        _h.Time.Advance(TimeSpan.FromHours(1));
        await _h.Service.Update(file.FileId, new JsonObject { ["Title"] = "b" });
        Assert.Equal(_h.Time.GetUtcNow(), _h.Reload(file.FileId).UpdatedDate);

        _h.Time.Advance(TimeSpan.FromHours(1));
        await _h.Service.Replace(file.FileId, FileServiceHarness.Text("n.txt", "new"));
        Assert.Equal(_h.Time.GetUtcNow(), _h.Reload(file.FileId).UpdatedDate);

        _h.Time.Advance(TimeSpan.FromHours(1));
        await _h.Service.Delete(file.FileId);
        Assert.Equal(_h.Time.GetUtcNow(), _h.Reload(file.FileId).UpdatedDate);
    }

    [Fact]
    public async Task AFailedUpdate_DoesNotTouchUpdatedDate()
    {
        var file = await _h.UploadTextAsync();
        var before = _h.Reload(file.FileId).UpdatedDate;
        _h.Time.Advance(TimeSpan.FromHours(1));
        _h.Service.RejectUpdate = true;

        await Assert.ThrowsAsync<ApiException>(() => _h.Service.Update(file.FileId, new File { Title = "a", FileType = FileType.Text }));

        Assert.Equal(before, _h.Reload(file.FileId).UpdatedDate);
    }

    [Fact]
    public async Task UpdatedBy_SetOnCurrentInPreUpdate_IsPersisted()
    {
        var file = await _h.UploadTextAsync();
        _h.Service.SetUpdatedByInPreUpdate = "alice";

        await _h.Service.Update(file.FileId, new File { Title = "a", FileType = FileType.Text });

        Assert.Equal("alice", _h.Reload(file.FileId).UpdatedBy);
    }
}

// E: bulk deletes call the collection hook once per operation and the per-file hook once per file.
public class FileServiceBulkHookCountTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task Delete_Ids_PurgeIds_DeleteQuery_CallEachHookExactlyOncePerItsContract()
    {
        var a = await _h.UploadTextAsync();
        var b = await _h.UploadTextAsync();
        var c = await _h.UploadTextAsync();
        var d = await _h.UploadTextAsync();
        var (aId, bId) = (a.FileId, b.FileId);
        _h.Service.Events.Clear();

        await _h.Service.Delete([aId, bId], CancellationToken.None);
        AssertCounts(collection: 1, perFile: 2);

        _h.Service.Events.Clear();
        await _h.Service.Purge([c.FileId, d.FileId]);
        AssertCounts(collection: 1, perFile: 2);

        _h.Service.Events.Clear();
        await _h.Service.Delete(f => f.FileId == aId || f.FileId == bId);
        AssertCounts(collection: 1, perFile: 2);
    }

    [Fact]
    public async Task SingleEntityOperations_CallOnlyThePerFileHook()
    {
        var a = await _h.UploadTextAsync();
        var b = await _h.UploadTextAsync();
        _h.Service.Events.Clear();

        await _h.Service.Delete(a.FileId);
        await _h.Service.Purge(b.FileId);

        Assert.Equal(2, _h.Service.Events.Count(e => e == "PreDelete"));
        Assert.Equal(2, _h.Service.Events.Count(e => e == "PostDelete"));
        Assert.DoesNotContain("PreDelete(many)", _h.Service.Events);
    }

    private void AssertCounts(int collection, int perFile)
    {
        Assert.Equal(collection, _h.Service.Events.Count(e => e == "PreDelete(many)"));
        Assert.Equal(collection, _h.Service.Events.Count(e => e == "PostDelete(many)"));
        Assert.Equal(perFile, _h.Service.Events.Count(e => e == "PreDelete"));
        Assert.Equal(perFile, _h.Service.Events.Count(e => e == "PostDelete"));
        // order: collection hook first, then the per-file hooks
        Assert.True(_h.Service.Events.IndexOf("PreDelete(many)") < _h.Service.Events.IndexOf("PreDelete"));
        Assert.True(_h.Service.Events.IndexOf("PostDelete(many)") < _h.Service.Events.IndexOf("PostDelete"));
    }
}

// F: CanDelete must not say "yes" for a missing or soft-deleted file.
public class FileServiceCanDeleteTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Fact]
    public async Task CanDelete_Id_IsTrueOnlyForAnExistingEnabledDeletableFile()
    {
        var file = await _h.UploadTextAsync();

        Assert.True(await _h.Service.CanDelete(file.FileId));
        Assert.False(await _h.Service.CanDelete(9999));

        _h.Service.RejectDelete = true;
        Assert.False(await _h.Service.CanDelete(file.FileId));

        _h.Service.RejectDelete = false;
        await _h.Service.Delete(file.FileId);
        Assert.False(await _h.Service.CanDelete(file.FileId));
    }

    [Fact]
    public async Task CanDelete_Ids_IsFalseWhenAnyIdIsMissingDisabledOrVetoed()
    {
        var a = await _h.UploadTextAsync();
        var b = await _h.UploadTextAsync();

        Assert.True(await _h.Service.CanDelete([a.FileId, b.FileId], CancellationToken.None));
        Assert.False(await _h.Service.CanDelete([a.FileId, 9999], CancellationToken.None));

        await _h.Service.Delete(b.FileId);
        Assert.False(await _h.Service.CanDelete([a.FileId, b.FileId], CancellationToken.None));

        _h.Service.RejectDelete = true;
        Assert.False(await _h.Service.CanDelete([a.FileId], CancellationToken.None));
    }
}

// G: every Unicode "format" character is invisible; none may stay in a name.
public class FileServiceFormatCharacterTests : IDisposable
{
    private readonly FileServiceHarness _h = new();

    public void Dispose() => _h.Dispose();

    [Theory]
    [InlineData('­')]
    [InlineData('᠎')]
    [InlineData('⁡')]
    [InlineData('⁢')]
    [InlineData('⁣')]
    [InlineData('⁤')]
    [InlineData('⁪')]
    [InlineData('‎')]
    [InlineData('؜')]
    public async Task Upload_FormatCharacters_AreStripped_FromTheStoredName(char format)
    {
        var file = await _h.Service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text($"pay{format}roll.txt"));

        Assert.Equal("payroll.txt", file.OriginalFileName);
    }
}
