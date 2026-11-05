using Amazon.S3.Model;

namespace TabTabGo.Storage.Aws;

/// <summary>Read-only stream over an S3 response that disposes the response (and its connection) with the stream.</summary>
internal sealed class S3ResponseStream : Stream
{
    private readonly IDisposable _owner;
    private readonly Stream _inner;
    private readonly long _contentLength;

    public S3ResponseStream(GetObjectResponse response)
        : this(response.ResponseStream, response, response.ContentLength)
    {
    }

    // The owner is what holds the HTTP connection (the response); it is disposed with the stream.
    internal S3ResponseStream(Stream inner, IDisposable owner, long contentLength)
    {
        _inner = inner;
        _owner = owner;
        _contentLength = contentLength;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _inner.CanSeek ? _inner.Length : _contentLength;

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

    public override int Read(Span<byte> buffer) => _inner.Read(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        _inner.ReadAsync(buffer, offset, count, cancellationToken);

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        _inner.ReadAsync(buffer, cancellationToken);

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
            _owner.Dispose();
        }

        base.Dispose(disposing);
    }
}
