using System.Net;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TabTabGo.Core.Data;
using TabTabGo.Core.Exceptions;
using TabTabGo.Data.EF;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.Services;
using TabTabGo.Storage.TestKit;
using TabTabGo.Storage.Testing;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Services.Tests;

/// <summary>In-memory provider that records every call and can be told to fail.</summary>
public class RecordingProvider : InMemoryStorageProvider
{
    public List<string> Calls { get; } = new();

    public Func<string, string, bool>? FailStoreWhen { get; set; }

    public bool FailDeletes { get; set; }

    public List<CancellationToken> Tokens { get; } = new();

    public override Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown")
    {
        lock (Calls) { Calls.Add("Store(bytes-generated)"); }
        return base.StoreAsync(buffer, extension, container);
    }

    public override Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown")
    {
        lock (Calls) { Calls.Add($"Store(bytes):{container}/{filePath}"); }
        return base.StoreAsync(filePath, buffer, container);
    }

    public override Task<string> StoreAsync(Stream content, string filePath, StorageWriteOptions? options = null,
        string container = "unknown", CancellationToken ct = default)
    {
        lock (Calls) { Calls.Add($"Store(stream):{container}/{filePath}"); Tokens.Add(ct); }
        if (FailStoreWhen?.Invoke(container, filePath) == true)
        {
            throw new StorageException("injected store failure", "Recording", "Store", container, filePath);
        }

        return base.StoreAsync(content, filePath, options, container, ct);
    }

    public override Task<Stream?> RetrieveAsync(string filePath, string container, CancellationToken ct)
    {
        lock (Calls) { Tokens.Add(ct); }
        return base.RetrieveAsync(filePath, container, ct);
    }

    public override Task DeleteAsync(string filePath, string container, CancellationToken ct)
    {
        lock (Calls) { Calls.Add($"Delete:{container}/{filePath}"); }
        if (FailDeletes)
        {
            throw new StorageException("injected delete failure", "Recording", "Delete", container, filePath);
        }

        return base.DeleteAsync(filePath, container, ct);
    }
}

public sealed class FailingUnitOfWork(Microsoft.EntityFrameworkCore.DbContext context)
    : UnitOfWork(context, Microsoft.Extensions.Logging.Abstractions.NullLogger<UnitOfWork>.Instance)
{
    public bool FailSave { get; set; }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default) =>
        FailSave ? throw new InvalidOperationException("injected save failure") : base.SaveChangesAsync(cancellationToken);
}

/// <summary>Records the lifecycle hooks and lets tests veto operations.</summary>
public class HookedFileService : FileService<File>
{
    public HookedFileService(IUnitOfWork uow, RecordingProvider storage, IImageService images, IOptions<StorageServiceOptions> options, TimeProvider time)
        : base(uow, new FakeLogger<FileService<File>>(), storage, images, options, time)
    {
    }

    public List<string> Events { get; } = new();

    public bool RejectCreate { get; set; }

    public bool RejectUpdate { get; set; }

    public bool RejectDelete { get; set; }

    public string? VetoTitle { get; set; }

    /// <summary>Authorization-style gate: every state the update gate sees must have this ReferenceId.</summary>
    public long? RequireReferenceId { get; set; }

    public bool RejectPreDelete { get; set; }

    public bool RejectPurge { get; set; }

    public long? PreUpdateCurrentReferenceId { get; set; }

    public (string Extension, string Name, string Path, long? Size, int Extra)? CreateSeen { get; private set; }

    /// <summary>Authorization-style hook: only allow updates of rows created by this user (judged on the entity the gate sees).</summary>
    public string? RequireCreatedBy { get; set; }

    public override bool IsValidToCreate(File entity, out string validationErrorMessage)
    {
        CreateSeen = (entity.FileExtension, entity.OriginalFileName, entity.FilePath, entity.FileSize, entity.ExtraProperties?.Count ?? 0);
        Events.Add("IsValidToCreate");
        validationErrorMessage = "vetoed";
        return !RejectCreate;
    }

    protected override Task PreCreate(File entity, CancellationToken cancellationToken = default)
    {
        Events.Add("PreCreate");
        return Task.CompletedTask;
    }

    protected override Task PostCreate(File entity, CancellationToken cancellationToken = default)
    {
        Events.Add("PostCreate");
        return Task.CompletedTask;
    }

    public override bool IsValidToUpdate(File? entity, out string validationErrorMessage)
    {
        Events.Add("IsValidToUpdate");
        validationErrorMessage = "vetoed";
        return !RejectUpdate && (VetoTitle is null || entity?.Title != VetoTitle) &&
               (RequireCreatedBy is null || entity?.CreatedBy == RequireCreatedBy) &&
               (RequireReferenceId is null || entity?.ReferenceId == RequireReferenceId);
    }

    public override Task PreUpdate(File current, File entity, CancellationToken cancellationToken)
    {
        PreUpdateCurrentReferenceId = current.ReferenceId;
        Events.Add("PreUpdate");
        return Task.CompletedTask;
    }

    public override Task PreUpdate(File current, JsonObject entity, CancellationToken cancellationToken)
    {
        PreUpdateCurrentReferenceId = current.ReferenceId;
        Events.Add("PreUpdate(json)");
        return Task.CompletedTask;
    }

    public override Task PreUpdate(File current, Microsoft.AspNetCore.JsonPatch.JsonPatchDocument<File> entity, CancellationToken cancellationToken)
    {
        PreUpdateCurrentReferenceId = current.ReferenceId;
        Events.Add("PreUpdate(patch)");
        return Task.CompletedTask;
    }

    public override Task PostUpdate(File entity, CancellationToken cancellationToken = default)
    {
        Events.Add("PostUpdate");
        return Task.CompletedTask;
    }

    public override bool IsValidToDelete(File entity, out string validationErrorMessage)
    {
        Events.Add("IsValidToDelete");
        validationErrorMessage = "vetoed";
        return !RejectDelete;
    }

    public override bool IsValidToPurge(File entity, out string validationErrorMessage)
    {
        Events.Add("IsValidToPurge");
        validationErrorMessage = "purge vetoed";
        return !RejectPurge && base.IsValidToPurge(entity, out validationErrorMessage);
    }

    protected override Task PreDelete(File entity, CancellationToken cancellationToken = default)
    {
        if (RejectPreDelete)
        {
            throw new ApiException("vetoed in PreDelete", null, Microsoft.Extensions.Logging.LogLevel.Warning, null, HttpStatusCode.BadRequest, "PRE_DELETE_VETO");
        }

        Events.Add("PreDelete");
        return Task.CompletedTask;
    }

    protected override Task PreDelete(IEnumerable<File> entity, CancellationToken cancellationToken = default)
    {
        Events.Add("PreDelete(many)");
        return Task.CompletedTask;
    }

    protected override Task PostDelete(File entity, CancellationToken cancellationToken = default)
    {
        Events.Add("PostDelete");
        return Task.CompletedTask;
    }

    protected override Task PostDelete(IEnumerable<File> entity, CancellationToken cancellationToken = default)
    {
        Events.Add("PostDelete(many)");
        return Task.CompletedTask;
    }
}

public sealed class FileServiceHarness : IDisposable
{
    public FileServiceHarness(Action<StorageServiceOptions>? configure = null, ImageServiceOptions? images = null)
    {
        Db = new SqliteStorageFixture();
        Uow = new FailingUnitOfWork(Db.Context);
        Storage = new RecordingProvider();
        Time = new FakeTimeProvider(new DateTimeOffset(2031, 5, 6, 7, 8, 9, TimeSpan.Zero));
        var options = new StorageServiceOptions();
        configure?.Invoke(options);
        Service = new HookedFileService(Uow, Storage, new ImageService(Options.Create(images ?? new ImageServiceOptions())),
            Options.Create(options), Time);
    }

    public SqliteStorageFixture Db { get; }

    public FailingUnitOfWork Uow { get; }

    public RecordingProvider Storage { get; }

    public FakeTimeProvider Time { get; }

    public HookedFileService Service { get; }

    public static File Meta(FileType type = FileType.Text) => new()
    {
        Title = "title", Comment = "comment", ReferenceType = "Ticket", ReferenceId = 7, CreatedBy = "tester", UpdatedBy = "tester",
        FileType = type,
    };

    public static FileUpload Text(string name = "note.txt", string content = "hello") =>
        FileUpload.FromBytes(System.Text.Encoding.UTF8.GetBytes(content), name, "text/plain");

    public static FileUpload PngUpload(string name = "pic.png", int w = 64, int h = 48) =>
        FileUpload.FromBytes(TestImages.Png(w, h), name, "image/png");

    public async Task<File> UploadTextAsync(string name = "note.txt")
    {
        var file = await Service.Upload(Meta(), Text(name));
        Service.Events.Clear();
        Storage.Calls.Clear();
        return file;
    }

    public async Task<File> UploadImageAsync(string name = "pic.png", FileUpload? upload = null)
    {
        var file = await Service.Upload(Meta(FileType.Image), upload ?? PngUpload(name));
        Service.Events.Clear();
        Storage.Calls.Clear();
        return file;
    }

    public File Reload(long id)
    {
        using var other = Db.NewContext();
        return other.Set<File>().Single(f => f.FileId == id);
    }

    public int RowCount()
    {
        using var other = Db.NewContext();
        return other.Set<File>().Count();
    }

    public void Dispose() => Db.Dispose();
}
