using System.Net;
using System.Text;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using TabTabGo.Storage.Exceptions;
using TabTabGo.Storage.FileStorage;
using TabTabGo.Storage.FileStorage.Services;
using TabTabGo.Storage.Models;
using TabTabGo.Storage.Services;
using TabTabGo.Storage.TestKit;
using Xunit;

namespace TabTabGo.Storage.FileStorage.Tests;

/// <summary>Builds a provider over a unique temporary content root.</summary>
public sealed class FileStorageHarness : IDisposable
{
    private readonly TempDirectory _temp = new();

    public FileStorageHarness(Action<PathSettings>? configure = null, TimeProvider? time = null)
    {
        Settings = new PathSettings { RootPath = "store" };
        configure?.Invoke(Settings);
        Environment = new TestHostEnvironment(_temp.Path);
        PathService = new PathService(Environment, Options.Create(Settings), new FakeLogger<PathService>());
        Provider = new FileStorageProvider(PathService, time, new FakeLogger<FileStorageProvider>());
    }

    public string ContentRoot => _temp.Path;

    public PathSettings Settings { get; }

    public TestHostEnvironment Environment { get; }

    public PathService PathService { get; }

    public FileStorageProvider Provider { get; }

    public string Disk(params string[] parts) => Path.Combine([ContentRoot, .. parts]);

    public void Dispose() => _temp.Dispose();
}

public class FileStorageContractTests : StorageProviderContractTests<FileStorageProvider>
{
    private FileStorageHarness? _harness;

    protected override Task<FileStorageProvider> CreateProviderAsync()
    {
        _harness = new FileStorageHarness();
        return Task.FromResult(_harness.Provider);
    }

    protected override Task CleanupAsync()
    {
        _harness?.Dispose();
        return Task.CompletedTask;
    }
}

public class FileStorageProviderTests : IDisposable
{
    private readonly FileStorageHarness _h = new();

    public void Dispose() => _h.Dispose();

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    // L1: a segment is at most 255 UTF-8 bytes.
    [Theory]
    [InlineData(255, true)]
    [InlineData(256, false)]
    public async Task Store_SegmentLength_BoundaryAt255Bytes(int length, bool accepted)
    {
        var path = "dir/" + new string('a', length);
        var task = _h.Provider.StoreAsync(path, Bytes("x"), "Image");

        if (accepted)
        {
            Assert.Equal(path, await task);
            Assert.True(File.Exists(_h.Disk("store", "Image", "dir", new string('a', length))));
        }
        else
        {
            var ex = await Assert.ThrowsAsync<StorageException>(() => task);
            Assert.Equal("STORAGE_INVALID_PATH", ex.ErrorCode);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, ex.HttpStatusCode);
        }
    }

    [Fact]
    public async Task Store_SegmentLength_CountsUtf8Bytes()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(
            () => _h.Provider.StoreAsync(new string('\u00e9', 128), Bytes("x"), "Image"));

        Assert.Equal("STORAGE_INVALID_PATH", ex.ErrorCode);
    }

    [Fact]
    public async Task Retrieve_OverLongSegment_Is400_NotIoFailure()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(
            () => _h.Provider.RetrieveAsync(new string('a', 300), "Image"));

        Assert.Equal("STORAGE_INVALID_PATH", ex.ErrorCode);
    }

    [Fact]
    public async Task GetPublicUrl_OverLongSegment_Is400()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(
            () => _h.Provider.GetPublicUrlAsync(new string('a', 300), new PublicUrlOptions(), "Image"));

        Assert.Equal("STORAGE_INVALID_PATH", ex.ErrorCode);
    }

    [Theory]
    [InlineData("a\u202eb.txt")]
    [InlineData("a\u200bb.txt")]
    public async Task Store_UnicodeFormatCharacter_Rejected(string path)
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => _h.Provider.StoreAsync(path, Bytes("x"), "Image"));

        Assert.Equal("STORAGE_INVALID_PATH", ex.ErrorCode);
    }

    // F-1
    [Fact]
    public async Task Store_DotDotSegment_Throws_AndWritesNothingOutsideRoot()
    {
        var outside = _h.Disk("escaped.txt");

        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.StoreAsync("../../escaped.txt", Bytes("x"), "Image"));

        Assert.False(File.Exists(outside));
    }

    [Fact]
    public async Task Store_RootedPath_Throws() =>
        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.StoreAsync("/tmp/ttg-rooted.txt", Bytes("x"), "Image"));

    [Fact]
    public async Task Retrieve_TraversalPath_Throws() =>
        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.RetrieveAsync("../../../etc/passwd", "Image"));

    [Fact]
    public async Task Delete_TraversalPath_Throws_AndKeepsSiblingFile()
    {
        var sibling = _h.Disk("victim.txt");
        await File.WriteAllTextAsync(sibling, "keep");

        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.DeleteAsync("../../victim.txt", "Image"));

        Assert.True(File.Exists(sibling));
    }

    [Fact]
    public async Task Store_BackslashPath_NormalisedUnderRoot()
    {
        var path = await _h.Provider.StoreAsync("a\\b\\c.txt", Bytes("x"), "Image");

        Assert.Equal("a/b/c.txt", path);
        Assert.True(File.Exists(_h.Disk("store", "Image", "a", "b", "c.txt")));
    }

    [Fact]
    public async Task Store_EncodedDotDot_TreatedLiterally()
    {
        await _h.Provider.StoreAsync("%2e%2e/x.txt", Bytes("x"), "Image");

        Assert.True(File.Exists(_h.Disk("store", "Image", "%2e%2e", "x.txt")));
    }

    [Fact]
    public async Task Store_UnsafeContainer_Throws() =>
        await Assert.ThrowsAsync<StorageException>(() => _h.Provider.StoreAsync("a.txt", Bytes("x"), "../outside"));

    // F-2
    [Fact]
    public async Task Store_NestedPath_CreatesDirectories()
    {
        await _h.Provider.StoreAsync("ThumbLarge/20260101/x.png", Bytes("x"), "Image");

        Assert.True(File.Exists(_h.Disk("store", "Image", "ThumbLarge", "20260101", "x.png")));
    }

    // F-3
    [Fact]
    public async Task StoreThenRetrieve_WorkingDirectoryDiffersFromContentRoot()
    {
        var path = await _h.Provider.StoreAsync("w.txt", Bytes("x"), "Image");

        Assert.True(File.Exists(_h.Disk("store", "Image", "w.txt")));
        Assert.False(File.Exists(Path.Combine(Directory.GetCurrentDirectory(), "store", "Image", "w.txt")));
        Assert.NotNull(await _h.Provider.RetrieveAsync(path, "Image"));
    }

    // F-4
    [Fact]
    public async Task Store_UnmappedContainer_UsesContainerFolder()
    {
        await _h.Provider.StoreAsync("t.txt", Bytes("x"), "WordDocument");

        Assert.True(File.Exists(_h.Disk("store", "WordDocument", "t.txt")));
    }

    [Fact]
    public async Task Store_MappedContainer_UsesMappedFolder()
    {
        using var h = new FileStorageHarness(s => s["Image"] = "pictures");

        await h.Provider.StoreAsync("m.png", Bytes("x"), "image");

        Assert.True(File.Exists(h.Disk("store", "pictures", "m.png")));
        Assert.Equal(Path.Combine("store", "pictures"), h.Provider.GetRootDirectory("Image"));
    }

    [Fact]
    public async Task Store_MappedFolderWithBackslashes_IsTreatedAsDirectories()
    {
        using var h = new FileStorageHarness(s => s["Image"] = "media\\pictures");

        await h.Provider.StoreAsync("a.png", Bytes("x"), "Image");

        Assert.True(File.Exists(h.Disk("store", "media", "pictures", "a.png")));
    }

    // F-6
    [Fact]
    public async Task Store_Overwrite_IsAtomic_NoTempFilesLeft_AndReadersNeverSeePartialContent()
    {
        var a = new string('a', 200_000);
        var b = new string('b', 100_000);
        await _h.Provider.StoreAsync("x.txt", Bytes(a), "Image");
        var stop = false;
        var torn = 0;
        var reader = Task.Run(async () =>
        {
            while (!Volatile.Read(ref stop))
            {
                await using var s = await _h.Provider.RetrieveAsync("x.txt", "Image");
                var length = s!.Length;
                if (length != a.Length && length != b.Length)
                {
                    Interlocked.Increment(ref torn);
                }
            }
        });

        for (var i = 0; i < 40; i++)
        {
            await _h.Provider.StoreAsync("x.txt", Bytes(i % 2 == 0 ? b : a), "Image");
        }

        Volatile.Write(ref stop, true);
        await reader;

        Assert.Equal(0, torn);
        Assert.Empty(Directory.GetFiles(_h.Disk("store", "Image"), "*.tmp"));
    }

    [Fact]
    public async Task Store_Concurrent50_AllDistinctAndReadable()
    {
        var paths = await Task.WhenAll(Enumerable.Range(0, 50)
            .Select(i => _h.Provider.StoreAsync(Bytes($"v{i}"), ".txt", "Text")));

        Assert.Equal(50, paths.Distinct().Count());
        for (var i = 0; i < 50; i++)
        {
            await using var s = await _h.Provider.RetrieveAsync(paths[i], "Text");
            Assert.Equal($"v{i}", new StreamReader(s!).ReadToEnd());
        }
    }

    // F-7
    [Fact]
    public async Task Store_DateFolder_UsesUtcFromTimeProvider()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2031, 2, 3, 23, 30, 0, TimeSpan.Zero));
        using var h = new FileStorageHarness(time: time);

        var path = await h.Provider.StoreAsync(Bytes("x"), ".png", "Image");

        Assert.StartsWith("20310203/", path);
    }

    [Fact]
    public async Task Store_DateFolder_HonoursConfiguredFormat()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2031, 2, 3, 1, 0, 0, TimeSpan.Zero));
        using var h = new FileStorageHarness(s => s.DateFormat = "yyyy-MM", time);

        var path = await h.Provider.StoreAsync(Bytes("x"), "png", "Image");

        Assert.StartsWith("2031-02/", path);
        Assert.EndsWith(".png", path);
    }

    [Fact]
    public async Task Store_GeneratedName_InvalidExtension_ThrowsBadRequest()
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => _h.Provider.StoreAsync(Bytes("x"), ".p/ng", "Image"));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
    }

    [Fact]
    public async Task Store_GeneratedName_NoExtension_HasNoDot()
    {
        var path = await _h.Provider.StoreAsync(Bytes("x"), string.Empty, "Image");

        Assert.DoesNotContain('.', Path.GetFileName(path));
    }

    // F-9
    [Fact]
    public async Task OpenRead_ReturnsFileStream_NotBuffered()
    {
        await _h.Provider.StoreAsync("big.bin", new byte[1024], "Image");

        await using var stream = await _h.Provider.OpenReadAsync("big.bin", "Image");

        Assert.IsType<FileStream>(stream);
    }

    // A reader streaming an object must not make a concurrent overwrite or delete fail (a sharing violation on Windows).
    [Fact]
    public async Task OpenRead_WhileTheObjectIsOverwrittenAndDeleted_DoesNotBlockTheWriter()
    {
        await _h.Provider.StoreAsync("live.bin", new byte[4096], "Image");
        await using var stream = await _h.Provider.OpenReadAsync("live.bin", "Image");
        Assert.NotNull(stream);

        await _h.Provider.StoreAsync("live.bin", Bytes("replaced"), "Image");
        await _h.Provider.DeleteAsync("live.bin", "Image");

        Assert.False(await _h.Provider.ExistsAsync("live.bin", "Image"));
    }

    [Fact]
    public async Task Exists_OnDirectory_IsFalse_AndRetrieveIsNull()
    {
        await _h.Provider.StoreAsync("dir/file.txt", Bytes("x"), "Image");

        Assert.False(await _h.Provider.ExistsAsync("dir", "Image"));
        Assert.Null(await _h.Provider.RetrieveAsync("dir", "Image"));
    }

    // QA: 2 of 8 concurrent no-overwrite stores won. Exactly one writer may win, on every OS.
    [Fact]
    public async Task Store_OverwriteFalse_8Concurrent_ExactlyOneWinner_25Rounds()
    {
        for (var round = 0; round < 25; round++)
        {
            var name = $"race-{round}.txt";
            var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(i => Task.Run(async () =>
            {
                using var content = new MemoryStream(Bytes(new string((char)('a' + i), 50_000)));
                try
                {
                    await _h.Provider.StoreAsync(content, name, new StorageWriteOptions { Overwrite = false }, "Text");
                    return true;
                }
                catch (StorageException e) when (e.ErrorCode == "STORAGE_OBJECT_EXISTS")
                {
                    return false;
                }
            })));

            Assert.Equal(1, results.Count(r => r));
            await using var stored = await _h.Provider.RetrieveAsync(name, "Text");
            var text = new StreamReader(stored!).ReadToEnd();
            Assert.Equal(50_000, text.Length);
            Assert.Single(text.Distinct());
        }

        Assert.Empty(Directory.GetFiles(_h.Disk("store", "Text"), "*.tmp"));
    }

    [Fact]
    public async Task Store_OverwriteFalse_FailedWrite_DoesNotLeaveAHalfFileBehind()
    {
        using var broken = new ThrowingStream();

        await Assert.ThrowsAsync<StorageException>(() =>
            _h.Provider.StoreAsync(broken, "half.txt", new StorageWriteOptions { Overwrite = false }, "Text"));

        Assert.False(File.Exists(_h.Disk("store", "Text", "half.txt")));
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("disk gone");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("disk gone");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public async Task Store_OverwriteFalse_LeavesNoTempFile()
    {
        await _h.Provider.StoreAsync("k.txt", Bytes("x"), "Image");
        using var s = new MemoryStream(Bytes("y"));

        await Assert.ThrowsAsync<StorageException>(() =>
            _h.Provider.StoreAsync(s, "k.txt", new StorageWriteOptions { Overwrite = false }, "Image"));

        Assert.Empty(Directory.GetFiles(_h.Disk("store", "Image"), "*.tmp"));
    }

    [Fact]
    [System.Runtime.Versioning.UnsupportedOSPlatform("windows")]
    public async Task Store_ReadOnlyDirectory_ThrowsStorageException()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "POSIX permissions only");
        var dir = _h.Disk("store", "Image");
        Directory.CreateDirectory(dir);
        File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        try
        {
            try
            {
                await File.WriteAllBytesAsync(Path.Combine(dir, "probe"), [1]);
                Assert.Skip("Running with rights that ignore directory permissions.");
            }
            catch (UnauthorizedAccessException)
            {
            }

            var ex = await Assert.ThrowsAsync<StorageException>(() => _h.Provider.StoreAsync("a.txt", Bytes("x"), "Image"));
            Assert.Equal(HttpStatusCode.BadGateway, ex.HttpStatusCode);
        }
        finally
        {
            File.SetUnixFileMode(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
    }

    // F-10
    [Fact]
    public async Task GetPublicUrl_WithBaseUrl_Combines()
    {
        using var h = new FileStorageHarness(s =>
        {
            s.PublicBaseUrl = "https://cdn.test/files/";
            s["Image"] = "pictures";
        });

        var uri = await h.Provider.GetPublicUrlAsync("2026/a b.png", new PublicUrlOptions(), "Image");

        Assert.Equal("https://cdn.test/files/pictures/2026/a%20b.png", uri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetPublicUrl_RootedMappedFolder_UsesContainerName()
    {
        using var h = new FileStorageHarness(s =>
        {
            s.PublicBaseUrl = "https://cdn.test";
            s["Image"] = "/srv/images";
        });

        var uri = await h.Provider.GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image");

        Assert.Equal("https://cdn.test/Image/a.png", uri!.AbsoluteUri);
    }

    [Fact]
    public async Task GetPublicUrl_NoBaseUrl_Null() =>
        Assert.Null(await _h.Provider.GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image"));

    [Fact]
    public async Task GetPublicUrl_NonHttpBaseUrl_Null()
    {
        using var h = new FileStorageHarness(s => s.PublicBaseUrl = "file:///tmp");

        Assert.Null(await h.Provider.GetPublicUrlAsync("a.png", new PublicUrlOptions(), "Image"));
    }

    [Fact]
    public async Task GetPublicUrl_UnsafePath_Throws() =>
        await Assert.ThrowsAsync<StorageException>(() =>
            _h.Provider.GetPublicUrlAsync("../x", new PublicUrlOptions(), "Image"));

    [Fact]
    public async Task GetPublicPath_Legacy_ReturnsRelativePath()
    {
#pragma warning disable CS0618
        Assert.Equal("a/b.png", await _h.Provider.GetPublicPath("a/b.png", "image/png", "b.png", 3, "Image"));
#pragma warning restore CS0618
    }

    [Fact]
    public void GetRootDirectory_UnsafeContainer_Throws() =>
        Assert.Throws<StorageException>(() => _h.Provider.GetRootDirectory("../x"));

    [Fact]
    public void Ctor_NullPathService_Throws() =>
        Assert.Throws<ArgumentNullException>(() => new FileStorageProvider(null!));

    [Fact]
    public async Task LegacySingleArgumentCtor_Works()
    {
        var provider = new FileStorageProvider(_h.PathService);

        var path = await provider.StoreAsync(Bytes("x"), ".txt", "Text");

        Assert.NotNull(await provider.RetrieveAsync(path, "Text"));
    }

    [Fact]
    public async Task Store_NullBuffer_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Provider.StoreAsync("a", (byte[])null!, "c"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Provider.StoreAsync((byte[])null!, ".a", "c"));
        await Assert.ThrowsAsync<ArgumentNullException>(() => _h.Provider.StoreAsync((Stream)null!, "a"));
    }
}
