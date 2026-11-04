using Amazon.S3.Model;
using Microsoft.Extensions.Logging.Testing;
using Microsoft.Extensions.Options;
using Xunit;

namespace TabTabGo.Storage.Aws.Tests;

public class S3ResponseStreamTests
{
    private sealed class TrackingStream(byte[] data) : MemoryStream(data)
    {
        public bool IsDisposed { get; private set; }

        protected override void Dispose(bool disposing)
        {
            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    [Fact]
    public async Task OpenRead_DisposingTheStream_DisposesTheGetObjectResponseStream()
    {
        var s3 = new FakeS3();
        TrackingStream? tracked = null;
        s3.ResponseStreamFactory = data => tracked = new TrackingStream(data);
        var provider = new S3StorageProvider(s3.Client.Object, Options.Create(new S3Configuration { BucketName = "b", Region = "eu-west-1" }),
            new FakeLogger<S3StorageProvider>());
        await provider.StoreAsync("a.txt", [1, 2, 3], "Text");

        var stream = await provider.OpenReadAsync("a.txt", "Text");
        Assert.False(tracked!.IsDisposed);
        await stream!.DisposeAsync();

        Assert.True(tracked.IsDisposed);
    }

    [Fact]
    public void SyncReadPaths_Length_AndFlush_ForwardToTheResponse()
    {
        var inner = new TrackingStream([1, 2, 3, 4, 5, 6]);
        var stream = new S3ResponseStream(new GetObjectResponse { ResponseStream = inner });

        var array = new byte[2];
        Assert.Equal(2, stream.Read(array, 0, 2));
        Assert.Equal(new byte[] { 1, 2 }, array);
        Span<byte> span = stackalloc byte[3];
        Assert.Equal(3, stream.Read(span));
        Assert.Equal(new byte[] { 3, 4, 5 }, span.ToArray());
        Assert.Equal(6, stream.Length);
        stream.Flush();
        Assert.True(stream.CanRead);
        Assert.False(stream.CanSeek);
        Assert.False(stream.CanWrite);
        stream.Dispose();
        Assert.True(inner.IsDisposed);
    }

    [Fact]
    public async Task AsyncReadPaths_ForwardToTheResponse()
    {
        var inner = new TrackingStream([9, 8, 7, 6]);
        await using var stream = new S3ResponseStream(new GetObjectResponse { ResponseStream = inner });

        var array = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(array, 0, 2, CancellationToken.None));
        Memory<byte> memory = new byte[2];
        Assert.Equal(2, await stream.ReadAsync(memory));
        Assert.Equal(new byte[] { 7, 6 }, memory.ToArray());
    }

    [Fact]
    public void NotSupportedMembers_Throw()
    {
        using var stream = new S3ResponseStream(new GetObjectResponse { ResponseStream = new MemoryStream() });

        Assert.Throws<NotSupportedException>(() => stream.Position = 1);
        Assert.Throws<NotSupportedException>(() => stream.Seek(0, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.SetLength(1));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
    }
}
