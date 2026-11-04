using System.Net;
using System.Text;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

public class ServicesDiTests : IDisposable
{
    private readonly SqliteStorageFixture _db = new();

    public void Dispose() => _db.Dispose();

    private ServiceProvider Build(Action<IServiceCollection>? extra = null, Action<StorageServiceOptions>? options = null,
        Action<ImageServiceOptions>? images = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IStorageProvider, InMemoryStorageProvider>();
        services.AddSingleton<IUnitOfWork>(new TestUnitOfWork(_db.Context));
        services.AddTabTabGoStorageServices<File>(options, images);
        extra?.Invoke(services);
        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    [Fact]
    public async Task AddTabTabGoStorageServices_ResolvesFileService()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        var service = scope.ServiceProvider.GetRequiredService<IFileService<File>>();
        var uploaded = await service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text());

        Assert.IsType<FileService<File>>(service);
        Assert.True(uploaded.FileId > 0);
        Assert.IsType<ImageService>(scope.ServiceProvider.GetRequiredService<IImageService>());
    }

    [Fact]
    public void FileService_IsScoped_ImageService_IsSingleton()
    {
        using var provider = Build();
        using var one = provider.CreateScope();
        using var two = provider.CreateScope();

        Assert.NotSame(one.ServiceProvider.GetRequiredService<IFileService<File>>(), two.ServiceProvider.GetRequiredService<IFileService<File>>());
        Assert.Same(one.ServiceProvider.GetRequiredService<IImageService>(), two.ServiceProvider.GetRequiredService<IImageService>());
    }

    [Fact]
    public void Options_AreConfigured()
    {
        using var provider = Build(options: o => o.MaxFileSizeBytes = 123, images: i => i.JpegQuality = 55);

        Assert.Equal(123, provider.GetRequiredService<IOptions<StorageServiceOptions>>().Value.MaxFileSizeBytes);
        Assert.Equal(55, provider.GetRequiredService<IOptions<ImageServiceOptions>>().Value.JpegQuality);
    }

    [Fact]
    public async Task ConfiguredOptions_AreAppliedByTheService()
    {
        using var provider = Build(options: o => o.MaxFileSizeBytes = 2);
        using var scope = provider.CreateScope();
        var service = scope.ServiceProvider.GetRequiredService<IFileService<File>>();

        var ex = await Assert.ThrowsAsync<ApiException>(() => service.Upload(FileServiceHarness.Meta(), FileServiceHarness.Text()));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, ex.HttpStatusCode);
    }

    [Fact]
    public void Defaults_DenyActiveContent_AndCapTheSize()
    {
        var options = new StorageServiceOptions();

        Assert.Equal(50L * 1024 * 1024, options.MaxFileSizeBytes);
        Assert.Empty(options.AllowedExtensions);
        foreach (var ext in new[] { "html", ".HTM", "svg", ".js", "mjs", "xml", ".xhtml", "xht", "xsl", "xslt", "exe", "dll" })
        {
            Assert.Contains(options.DeniedExtensions, d => string.Equals(d.TrimStart('.'), ext.TrimStart('.'), StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal(10, StorageServiceOptions.MaxExtensionLength);
    }

    [Fact]
    public void DoesNotReplaceHostRegisteredImageService()
    {
        var custom = new ImageService();
        using var provider = Build(s => s.AddSingleton<IImageService>(custom));

        Assert.Same(custom, provider.GetRequiredService<IImageService>());
    }

    [Fact]
    public void LegacyRegisterStorageServices_StillRegistersItsServices()
    {
        var services = new ServiceCollection();
#pragma warning disable CS0618
        services.RegisterStorageServices();
#pragma warning restore CS0618

        Assert.Contains(services, d => d.ServiceType == typeof(IImageService) && d.ImplementationType == typeof(ImageService));
        Assert.Contains(services, d => d.ServiceType == typeof(IFileService<IFile>));
    }

    [Fact]
    public void NullServices_Throws() =>
        Assert.Throws<ArgumentNullException>(() => ((IServiceCollection)null!).AddTabTabGoStorageServices<File>());
}

public class FormFileExtensionsTests
{
    [Fact]
    public async Task ToFileUpload_CarriesNameTypeLength_AndOpensLazily()
    {
        var bytes = Encoding.UTF8.GetBytes("form file");
        var form = new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "upload.txt")
        {
            Headers = new HeaderDictionary(),
            ContentType = "text/plain",
        };

        var upload = form.ToFileUpload();

        Assert.Equal("upload.txt", upload.FileName);
        Assert.Equal("text/plain", upload.ContentType);
        Assert.Equal(bytes.Length, upload.Length);
        await using var stream = upload.OpenReadStream();
        Assert.Equal("form file", new StreamReader(stream).ReadToEnd());
    }

    [Fact]
    public void ToFileUpload_Null_Throws() => Assert.Throws<ArgumentNullException>(() => ((IFormFile)null!).ToFileUpload());

    [Fact]
    public async Task ToFileUpload_FeedsFileService()
    {
        using var h = new FileServiceHarness();
        var form = new FormFile(new MemoryStream([1, 2, 3]), 0, 3, "f", "x.bin") { Headers = new HeaderDictionary(), ContentType = "application/x" };

        var file = await h.Service.Upload(FileServiceHarness.Meta(), form.ToFileUpload());

        Assert.Equal(3, file.FileSize);
    }
}

// A consumer entity (like Techio's AssetFile) that lacks ReferenceType / ReferenceId / Duration.
public class MiniFile : Entity, IFile
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
}

public class ConsumerEntityTests : IDisposable
{
    private readonly Microsoft.Data.Sqlite.SqliteConnection _connection = new("DataSource=:memory:");
    private readonly MiniContext _context;

    public ConsumerEntityTests()
    {
        _connection.Open();
        _context = new MiniContext(new DbContextOptionsBuilder<MiniContext>().UseSqlite(_connection).Options);
        _context.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _context.Dispose();
        _connection.Dispose();
    }

    private FileService<MiniFile> Service() =>
        new(new TestUnitOfWork(_context), NullLogger<FileService<MiniFile>>.Instance, new InMemoryStorageProvider(), new ImageService());

    [Fact]
    public async Task Upload_WorksForAnEntityWithoutReferenceFields()
    {
        var file = await Service().Upload(new MiniFile { Title = "t", CreatedBy = "u", UpdatedBy = "u", FileType = FileType.Text },
            FileServiceHarness.Text());

        Assert.True(file.FileId > 0);
    }

    [Fact]
    public async Task ListAsync_FilterByReference_NotSupported_400()
    {
        var ex = await Assert.ThrowsAsync<ApiException>(() => Service().ListAsync(new FileListQuery { ReferenceType = "x" }));

        Assert.Equal("FILTER_NOT_SUPPORTED", ex.ErrorCode);
        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
    }

    [Fact]
    public async Task Update_IgnoresReferenceFields_WhenEntityHasNone()
    {
        var service = Service();
        var file = await service.Upload(new MiniFile { Title = "t", CreatedBy = "u", UpdatedBy = "u", FileType = FileType.Text },
            FileServiceHarness.Text());

        var updated = await service.Update(file.FileId, System.Text.Json.Nodes.JsonNode.Parse("""{"ReferenceType":"x","Title":"n"}""")!.AsObject());

        Assert.Equal("n", updated.Title);
    }

    [Fact]
    public async Task ListAsync_ByFileType_Works()
    {
        var service = Service();
        await service.Upload(new MiniFile { Title = "t", CreatedBy = "u", UpdatedBy = "u", FileType = FileType.Text }, FileServiceHarness.Text());

        var page = await service.ListAsync(new FileListQuery { FileType = FileType.Text });

        Assert.Equal(1, page.TotalItems);
    }

    private sealed class MiniContext(DbContextOptions<MiniContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.Entity<MiniFile>(e =>
            {
                e.HasKey(f => f.FileId);
                e.Property(f => f.FileId).ValueGeneratedOnAdd();
                e.Property(f => f.FileType).HasConversion(f => (int)f, f => (FileType)f);
                e.Property(f => f.CreatedBy).IsRequired();
                e.Property(f => f.UpdatedBy).IsRequired();
                e.Property(f => f.ExtraProperties).HasConversion(
                    v => System.Text.Json.JsonSerializer.Serialize(v, (System.Text.Json.JsonSerializerOptions?)null),
                    v => System.Text.Json.JsonSerializer.Deserialize<IDictionary<string, object>>(v, (System.Text.Json.JsonSerializerOptions?)null)
                         ?? new Dictionary<string, object>());
            });
    }
}
