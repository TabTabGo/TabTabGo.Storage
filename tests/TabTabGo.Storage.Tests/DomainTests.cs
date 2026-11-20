using System.Reflection;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Enums;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.Services;
using TabTabGo.Storage.TestKit;
using TabTabGo.Storage.Testing;
using TabTabGo.Storage.ViewModels;
using Xunit;
using File = TabTabGo.Storage.Entities.File;

namespace TabTabGo.Storage.Tests;

public class PathSettingsTests
{
    [Fact]
    public void Indexer_UnknownKey_ReturnsNull() => Assert.Null(new PathSettings()["Nope"]);

    [Fact]
    public void Indexer_IsCaseInsensitive()
    {
        var settings = new PathSettings { ["Image"] = "img" };

        Assert.Equal("img", settings["image"]);
        Assert.Equal("img", settings["IMAGE"]);
    }

    [Fact]
    public void Indexer_Set_ReplacesExisting()
    {
        var settings = new PathSettings { ["Image"] = "a" };
        settings["Image"] = "b";

        Assert.Equal("b", settings["Image"]);
        Assert.Single(settings.FileTypePaths);
    }

    [Fact]
    public void Indexer_SetNull_Throws() => Assert.Throws<ArgumentNullException>(() => new PathSettings()["x"] = null!);

    [Fact]
    public void TryGetPath_FindsKeyEvenWhenDictionaryWasReplacedWithCaseSensitiveOne()
    {
        var settings = new PathSettings { FileTypePaths = new Dictionary<string, string> { ["Image"] = "img" } };

        Assert.True(settings.TryGetPath("image", out var path));
        Assert.Equal("img", path);
        Assert.False(settings.TryGetPath("pdf", out _));
        Assert.False(settings.TryGetPath(null!, out _));
    }

    [Fact]
    public void Defaults()
    {
        var settings = new PathSettings();

        Assert.Equal(string.Empty, settings.RootPath);
        Assert.Null(settings.DateFormat);
        Assert.Null(settings.PublicBaseUrl);
    }

    [Fact]
    public void BindsFromConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RootPath"] = "files",
            ["DateFormat"] = "yyyy",
            ["PublicBaseUrl"] = "https://cdn.test/f",
            ["FileTypePaths:Image"] = "img",
            ["FileTypePaths:PDF"] = "pdf",
        }).Build();
        var settings = new PathSettings();

        configuration.Bind(settings);

        Assert.Equal("files", settings.RootPath);
        Assert.Equal("yyyy", settings.DateFormat);
        Assert.Equal("https://cdn.test/f", settings.PublicBaseUrl);
        Assert.Equal("img", settings["image"]);
        Assert.Equal("pdf", settings["Pdf"]);
    }
}

public class FileResultTests
{
    [Fact]
    public async Task DisposeAsync_DisposesStream()
    {
        var stream = new MemoryStream([1]);
        var result = new FileResult { Stream = stream, ContentType = "image/png" };

        await result.DisposeAsync();

        Assert.False(stream.CanRead);
        Assert.Equal("image/png", result.ContentType);
    }

    [Fact]
    public void Defaults_AreSafe()
    {
        var result = new FileResult();

        Assert.Same(Stream.Null, result.Stream);
        Assert.Equal("application/octet-stream", result.ContentType);
    }
}

public class EntityTests
{
    [Fact]
    public void File_ImplementsIFile_AndDefaults()
    {
        var file = new File();

        Assert.IsAssignableFrom<IFile>(file);
        Assert.Equal(FileType.Unknown, file.FileType);
        Assert.True(file.IsEnabled);
        Assert.Null(file.FilePath);
    }

    [Fact]
    public void File_RoundTripsThroughSystemTextJson_IncludingExtraProperties()
    {
        var file = new File
        {
            FileId = 5,
            Title = "t",
            FilePath = "a/b.png",
            FileType = FileType.Image,
            FileSize = 12,
            ReferenceId = 9,
            Duration = 3,
            IsCompressed = true,
        };
        file.ExtraProperties["k"] = "v";

        var json = JsonSerializer.Serialize(file);
        var back = JsonSerializer.Deserialize<File>(json)!;

        Assert.Equal(5, back.FileId);
        Assert.Equal("a/b.png", back.FilePath);
        Assert.Equal(FileType.Image, back.FileType);
        Assert.Equal(9, back.ReferenceId);
        Assert.Equal(3, back.Duration);
        Assert.Equal("v", back.ExtraProperties["k"]!.ToString());
    }

    [Fact]
    public void Assembly_HasNoAspNetCoreReference()
    {
        var references = typeof(IStorageProvider).Assembly.GetReferencedAssemblies().Select(a => a.Name!).ToList();

        Assert.DoesNotContain(references, name => name.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal));
    }

    [Fact]
    public void IFile_HasNoFormFile() => Assert.Null(typeof(IFile).GetProperty("FormFile"));
}

// A foreign implementation that only knows the 6.x members: the new members must work through the default methods.
#pragma warning disable CS0618
public class DefaultInterfaceMethodTests
{
    private sealed class LegacyProvider : IStorageProvider
    {
        public Dictionary<string, byte[]> Data { get; } = new();

        public string? PublicPath { get; set; }

        public string GetRootDirectory(string container = "unknown") => container;

        public Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown") =>
            StoreAsync("gen" + extension, buffer, container);

        public Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown")
        {
            Data[container + "/" + filePath] = buffer;
            return Task.FromResult(filePath);
        }

        public Task<Stream?> RetrieveAsync(string filePath, string container = "unknown") =>
            Task.FromResult<Stream?>(Data.TryGetValue(container + "/" + filePath, out var d) ? new MemoryStream(d) : null);

        public Task DeleteAsync(string filePath, string container = "unknown")
        {
            Data.Remove(container + "/" + filePath);
            return Task.CompletedTask;
        }

        public Task<string?> GetPublicPath(string filePath, string mediaType, string fileName, int expiryDays = 20,
            string container = "unknown") => Task.FromResult(PublicPath);
    }

    [Fact]
    public async Task StoreAsync_Stream_DelegatesToByteOverload()
    {
        IStorageProvider provider = new LegacyProvider();
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("abc"));

        var path = await provider.StoreAsync(content, "x.txt", null, "c", CancellationToken.None);

        Assert.Equal("x.txt", path);
        Assert.Equal("abc", Encoding.UTF8.GetString(((LegacyProvider)provider).Data["c/x.txt"]));
    }

    [Fact]
    public async Task RetrieveAsync_WithToken_DelegatesToLegacyOverload()
    {
        IStorageProvider provider = new LegacyProvider();
        await provider.StoreAsync("a.txt", [1], "c");

        Assert.NotNull(await provider.RetrieveAsync("a.txt", "c", CancellationToken.None));
        Assert.Null(await provider.RetrieveAsync("b.txt", "c", CancellationToken.None));
    }

    [Fact]
    public async Task OpenReadAsync_And_ExistsAsync_UseRetrieve()
    {
        IStorageProvider provider = new LegacyProvider();
        await provider.StoreAsync("a.txt", [1], "c");

        Assert.NotNull(await provider.OpenReadAsync("a.txt", "c"));
        Assert.Null(await provider.OpenReadAsync("zz.txt", "c"));
        Assert.True(await provider.ExistsAsync("a.txt", "c"));
        Assert.False(await provider.ExistsAsync("zz.txt", "c"));
    }

    [Fact]
    public async Task DeleteAsync_WithToken_DelegatesToLegacyOverload()
    {
        IStorageProvider provider = new LegacyProvider();
        await provider.StoreAsync("a.txt", [1], "c");

        await provider.DeleteAsync("a.txt", "c", CancellationToken.None);

        Assert.False(await provider.ExistsAsync("a.txt", "c"));
    }

    [Theory]
    [InlineData("https://host/x?sig=1", true)]
    [InlineData("http://host/x", true)]
    [InlineData("/relative/path", false)]
    [InlineData("a/b.png", false)]
    [InlineData(null, false)]
    public async Task GetPublicUrlAsync_ParsesAbsoluteHttpOnly(string? legacy, bool expectUri)
    {
        IStorageProvider provider = new LegacyProvider { PublicPath = legacy };

        var uri = await provider.GetPublicUrlAsync("a.png", new PublicUrlOptions());

        Assert.Equal(expectUri, uri is not null);
    }

    [Fact]
    public async Task GetPublicUrlAsync_NullOptions_Throws()
    {
        IStorageProvider provider = new LegacyProvider();

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.GetPublicUrlAsync("a", null!));
    }

    [Fact]
    public async Task StoreAsync_NullStream_Throws()
    {
        IStorageProvider provider = new LegacyProvider();

        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.StoreAsync((Stream)null!, "a"));
    }
}
#pragma warning restore CS0618

public class InMemoryStorageProviderTests : StorageProviderContractTests<InMemoryStorageProvider>
{
    protected override Task<InMemoryStorageProvider> CreateProviderAsync() =>
        Task.FromResult(new InMemoryStorageProvider());

    [Fact]
    public async Task Store_RecordsContentTypeFromExtension_OrOptions()
    {
        await Provider.StoreAsync("a.png", [1], Container);
        using var stream = new MemoryStream([1]);
        await Provider.StoreAsync(stream, "b.bin", new StorageWriteOptions { ContentType = "x/y" }, Container);

        Assert.Equal("image/png", Provider.GetContentType("a.png", Container));
        Assert.Equal("x/y", Provider.GetContentType("b.bin", Container));
        Assert.Null(Provider.GetContentType("missing", Container));
    }

    [Fact]
    public async Task Count_And_Keys_ReflectStoredObjects()
    {
        await Provider.StoreAsync("a.txt", [1], Container);
        await Provider.StoreAsync("b.txt", [1], Container);

        Assert.Equal(2, Provider.Count);
        Assert.Contains($"{Container}/a.txt", Provider.Keys());
    }

    [Fact]
    public void GetRootDirectory_IsContainerPrefix() => Assert.Equal("Image/", Provider.GetRootDirectory("Image"));

    [Fact]
    public async Task GetPublicUrlAsync_ReturnsDeterministicHttpsUrl()
    {
        var uri = await Provider.GetPublicUrlAsync("a/b.png", new PublicUrlOptions(), Container);
#pragma warning disable CS0618
        var legacy = await Provider.GetPublicPath("a/b.png", "image/png", "b.png", 1, Container);
#pragma warning restore CS0618

        Assert.Equal($"https://in-memory.invalid/{Container}/a/b.png", uri!.ToString());
        Assert.Equal(uri.ToString(), legacy);
    }

    [Fact]
    public async Task Store_GeneratedName_WithoutExtension_HasNoDot()
    {
        var path = await Provider.StoreAsync([1], string.Empty, Container);

        Assert.DoesNotContain('.', path);
    }
}

public class StorageProviderBaseTests
{
    private sealed class Minimal : StorageProvider
    {
        public override string GetRootDirectory(string container = "unknown") => container;

        public override Task<string> StoreAsync(byte[] buffer, string extension = ".tmp", string container = "unknown") =>
            Task.FromResult("g" + extension);

        public override Task<string> StoreAsync(string filePath, byte[] buffer, string container = "unknown") =>
            Task.FromResult(filePath);

        public override Task<Stream?> RetrieveAsync(string filePath, string container = "unknown") =>
            Task.FromResult<Stream?>(filePath == "here" ? new MemoryStream([1]) : null);

        public override Task DeleteAsync(string filePath, string container = "unknown") => Task.CompletedTask;

        public override Task<string?> GetPublicPath(string filePath, string mediaType, string fileName, int expiryDays = 20,
            string container = "unknown") => Task.FromResult<string?>("https://h/" + filePath);

        public string Norm(string? path) => NormalizePath("T", "Op", "c", path);

        public string Cont(string? container) => NormalizeContainer("T", "Op", container);
    }

    [Fact]
    public async Task VirtualMembers_DelegateToLegacyAbstracts()
    {
        var provider = new Minimal();

        Assert.Equal("x", await provider.StoreAsync(new MemoryStream([1]), "x"));
        Assert.True(await provider.ExistsAsync("here"));
        Assert.False(await provider.ExistsAsync("gone"));
        Assert.NotNull(await provider.OpenReadAsync("here"));
        Assert.Null(await provider.RetrieveAsync("gone", "c", CancellationToken.None));
        await provider.DeleteAsync("here", "c", CancellationToken.None);
        Assert.Equal("https://h/here", (await provider.GetPublicUrlAsync("here", new PublicUrlOptions()))!.ToString());
    }

    [Fact]
    public void NormalizePath_UnsafePath_ThrowsBadRequestStorageException()
    {
        var provider = new Minimal();

        var ex = Assert.Throws<Exceptions.StorageException>(() => provider.Norm("../x"));

        Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.HttpStatusCode);
        Assert.Equal("a/b", provider.Norm("a\\b"));
        Assert.Throws<Exceptions.StorageException>(() => provider.Norm(null));
    }

    [Fact]
    public void NormalizeContainer_RejectsUnsafeNames()
    {
        var provider = new Minimal();

        Assert.Equal("Image", provider.Cont("Image"));
        Assert.Throws<Exceptions.StorageException>(() => provider.Cont("../x"));
        Assert.Throws<Exceptions.StorageException>(() => provider.Cont(null));
    }
}
