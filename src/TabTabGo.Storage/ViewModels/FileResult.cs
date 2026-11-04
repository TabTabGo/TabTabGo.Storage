namespace TabTabGo.Storage.ViewModels;

/// <summary>A file read from storage. The caller owns the stream and must dispose the result.</summary>
public class FileResult : IAsyncDisposable
{
    /// <summary>Content stream. Disposed by <see cref="DisposeAsync"/>.</summary>
    public Stream Stream { get; set; } = Stream.Null;

    /// <summary>Content type, derived from the stored extension (never from the client).</summary>
    public string ContentType { get; set; } = "application/octet-stream";

    /// <inheritdoc />
    public ValueTask DisposeAsync()
    {
        GC.SuppressFinalize(this);
        return Stream.DisposeAsync();
    }
}
