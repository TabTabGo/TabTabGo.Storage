using System.Net;
using System.Text;
using TabTabGo.Storage.Exceptions;
using Xunit;

namespace TabTabGo.Storage.TestKit;

/// <summary>
/// The provider contract (ADR 0002), run against every <see cref="IStorageProvider"/>: null only for not-found,
/// idempotent delete, overwrite by default, seekable retrieve at position 0, unsafe paths rejected with 400.
/// </summary>
/// <typeparam name="TProvider">Provider under test.</typeparam>
public abstract class StorageProviderContractTests<TProvider> : IAsyncLifetime
    where TProvider : IStorageProvider
{
    /// <summary>The provider under test.</summary>
    protected TProvider Provider { get; private set; } = default!;

    /// <summary>A container unique to this test instance, valid for every backend.</summary>
    protected string Container { get; } = "c" + Guid.NewGuid().ToString("N");

    /// <summary>Creates the provider (and any backing resources) for one test.</summary>
    protected abstract Task<TProvider> CreateProviderAsync();

    /// <summary>Releases resources created by <see cref="CreateProviderAsync"/>.</summary>
    protected virtual Task CleanupAsync() => Task.CompletedTask;

    /// <inheritdoc />
    public async ValueTask InitializeAsync() => Provider = await CreateProviderAsync();

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await CleanupAsync();
        GC.SuppressFinalize(this);
    }

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static async Task<byte[]> ReadAllAsync(Stream? stream)
    {
        Assert.NotNull(stream);
        await using (stream)
        {
            using var copy = new MemoryStream();
            await stream.CopyToAsync(copy);
            return copy.ToArray();
        }
    }

    [Fact]
    public async Task Store_ByPath_ReturnsRelativePath_AndRetrieveRoundTrips()
    {
        var path = await Provider.StoreAsync("docs/a.txt", Bytes("hello"), Container);

        Assert.Equal("docs/a.txt", path);
        Assert.Equal(Bytes("hello"), await ReadAllAsync(await Provider.RetrieveAsync(path, Container)));
    }

    [Fact]
    public async Task Store_GeneratedName_KeepsExtension_AndIsRetrievable()
    {
        var path = await Provider.StoreAsync(Bytes("png-bytes"), ".png", Container);

        Assert.EndsWith(".png", path);
        Assert.False(string.IsNullOrWhiteSpace(path));
        Assert.Equal(Bytes("png-bytes"), await ReadAllAsync(await Provider.RetrieveAsync(path, Container)));
    }

    [Fact]
    public async Task Store_Stream_RoundTrips()
    {
        using var source = new MemoryStream(Bytes("streamed"));

        var path = await Provider.StoreAsync(source, "s/streamed.txt", null, Container, CancellationToken.None);

        Assert.Equal("s/streamed.txt", path);
        Assert.Equal(Bytes("streamed"), await ReadAllAsync(await Provider.RetrieveAsync(path, Container)));
    }

    [Fact]
    public async Task Store_EmptyContent_RoundTrips()
    {
        await Provider.StoreAsync("empty.bin", [], Container);

        Assert.Empty(await ReadAllAsync(await Provider.RetrieveAsync("empty.bin", Container)));
    }

    [Fact]
    public async Task Retrieve_ReturnsSeekableStreamAtPositionZero()
    {
        await Provider.StoreAsync("pos.txt", Bytes("0123456789"), Container);

        await using var stream = await Provider.RetrieveAsync("pos.txt", Container);

        Assert.NotNull(stream);
        Assert.True(stream.CanSeek);
        Assert.Equal(0, stream.Position);
        Assert.Equal(10, stream.Length);
    }

    [Fact]
    public async Task Retrieve_Missing_ReturnsNull()
    {
        Assert.Null(await Provider.RetrieveAsync("nothing/here.txt", Container));
        Assert.Null(await Provider.RetrieveAsync("nothing/here.txt", Container, CancellationToken.None));
    }

    [Fact]
    public async Task OpenRead_Existing_ReadsContent()
    {
        await Provider.StoreAsync("o.txt", Bytes("open"), Container);

        Assert.Equal(Bytes("open"), await ReadAllAsync(await Provider.OpenReadAsync("o.txt", Container)));
    }

    [Fact]
    public async Task OpenRead_Missing_ReturnsNull() =>
        Assert.Null(await Provider.OpenReadAsync("missing.txt", Container));

    [Fact]
    public async Task Exists_ReflectsPresence()
    {
        Assert.False(await Provider.ExistsAsync("e.txt", Container));

        await Provider.StoreAsync("e.txt", Bytes("x"), Container);

        Assert.True(await Provider.ExistsAsync("e.txt", Container));
    }

    [Fact]
    public async Task Delete_Existing_RemovesObject()
    {
        await Provider.StoreAsync("d.txt", Bytes("x"), Container);

        await Provider.DeleteAsync("d.txt", Container);

        Assert.Null(await Provider.RetrieveAsync("d.txt", Container));
        Assert.False(await Provider.ExistsAsync("d.txt", Container));
    }

    [Fact]
    public async Task Delete_Missing_IsNoOp()
    {
        await Provider.DeleteAsync("never-existed.txt", Container);
        await Provider.DeleteAsync("never-existed.txt", Container, CancellationToken.None);
    }

    [Fact]
    public async Task Store_ExistingPath_OverwritesByDefault()
    {
        await Provider.StoreAsync("o/over.txt", Bytes("first-longer-content"), Container);

        await Provider.StoreAsync("o/over.txt", Bytes("second"), Container);

        Assert.Equal(Bytes("second"), await ReadAllAsync(await Provider.RetrieveAsync("o/over.txt", Container)));
    }

    [Fact]
    public async Task Store_OverwriteFalse_ExistingObject_Throws()
    {
        await Provider.StoreAsync("keep.txt", Bytes("original"), Container);
        using var replacement = new MemoryStream(Bytes("replacement"));

        var ex = await Assert.ThrowsAsync<StorageException>(() => Provider.StoreAsync(
            replacement, "keep.txt", new Models.StorageWriteOptions { Overwrite = false }, Container,
            CancellationToken.None));

        Assert.Equal(HttpStatusCode.Conflict, ex.HttpStatusCode);
        Assert.Equal(Bytes("original"), await ReadAllAsync(await Provider.RetrieveAsync("keep.txt", Container)));
    }

    [Fact]
    public async Task Store_NestedPath_CreatesParents()
    {
        var path = await Provider.StoreAsync("ThumbLarge/20260101/deep/x.png", Bytes("t"), Container);

        Assert.Equal("ThumbLarge/20260101/deep/x.png", path);
        Assert.True(await Provider.ExistsAsync(path, Container));
    }

    [Fact]
    public async Task Store_BackslashPath_IsNormalisedToForwardSlash()
    {
        var path = await Provider.StoreAsync("a\\b\\c.txt", Bytes("x"), Container);

        Assert.Equal("a/b/c.txt", path);
        Assert.True(await Provider.ExistsAsync("a/b/c.txt", Container));
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../escape.txt")]
    [InlineData("/rooted.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("C:/drive.txt")]
    [InlineData("a/./b.txt")]
    [InlineData("")]
    public async Task Store_UnsafePath_ThrowsBadRequest(string path)
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => Provider.StoreAsync(path, Bytes("x"), Container));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("/etc/passwd")]
    public async Task Retrieve_UnsafePath_ThrowsBadRequest(string path)
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => Provider.RetrieveAsync(path, Container));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
    }

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("a/../../b")]
    public async Task Delete_UnsafePath_ThrowsBadRequest(string path)
    {
        var ex = await Assert.ThrowsAsync<StorageException>(() => Provider.DeleteAsync(path, Container));

        Assert.Equal(HttpStatusCode.BadRequest, ex.HttpStatusCode);
    }

    [Fact]
    public async Task Containers_AreIsolated()
    {
        var other = Container + "x";
        await Provider.StoreAsync("same.txt", Bytes("one"), Container);

        Assert.Null(await Provider.RetrieveAsync("same.txt", other));
    }

    [Fact]
    public async Task Store_CancelledToken_Throws()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using var content = new MemoryStream(Bytes("x"));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider.StoreAsync(content, "cancelled.txt", null, Container, cts.Token));
    }

    [Fact]
    public async Task Store_50InParallel_AllReadable()
    {
        var tasks = Enumerable.Range(0, 50).Select(i => Provider.StoreAsync($"p/{i}.txt", Bytes($"item-{i}"), Container));

        await Task.WhenAll(tasks);

        for (var i = 0; i < 50; i++)
        {
            Assert.Equal(Bytes($"item-{i}"), await ReadAllAsync(await Provider.RetrieveAsync($"p/{i}.txt", Container)));
        }
    }
}
