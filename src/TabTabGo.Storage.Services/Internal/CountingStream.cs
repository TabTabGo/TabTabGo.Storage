using System.Net;
using Microsoft.Extensions.Logging;
using TabTabGo.Core.Exceptions;

namespace TabTabGo.Storage.Services.Internal;

/// <summary>Read-only wrapper that counts bytes and refuses to read past a size limit (413).</summary>
internal sealed class CountingStream(Stream inner, long? maxBytes) : Stream
{
    public long BytesRead { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => BytesRead;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => Count(inner.Read(buffer, offset, count));

    public override int Read(Span<byte> buffer) => Count(inner.Read(buffer));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Count(await inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Count(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    internal static ApiException TooLarge(long limit) =>
        new($"The file exceeds the {limit} byte limit.", null, LogLevel.Warning, null,
            HttpStatusCode.RequestEntityTooLarge, "FILE_TOO_LARGE");

    private int Count(int read)
    {
        BytesRead += read;
        if (maxBytes is { } limit && BytesRead > limit)
        {
            throw TooLarge(limit);
        }

        return read;
    }
}
