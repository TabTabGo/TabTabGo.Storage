namespace TabTabGo.Storage.Models;

/// <summary>
/// Content to store. Carries no web-framework type, so the domain package stays free of ASP.NET Core
/// (an <c>IFormFile</c> adapter lives in <c>TabTabGo.Storage.Services</c>).
/// </summary>
public sealed class FileUpload
{
    private readonly Func<Stream> _open;
    private bool _consumed;

    private FileUpload(Func<Stream> open, long? length, string fileName, string? contentType)
    {
        _open = open;
        Length = length;
        FileName = fileName;
        ContentType = contentType;
    }

    /// <summary>File name as supplied by the client (unsanitised; the service sanitises it).</summary>
    public string FileName { get; }

    /// <summary>Media type declared by the client, if any.</summary>
    public string? ContentType { get; }

    /// <summary>Content length in bytes, or <c>null</c> when the source cannot tell.</summary>
    public long? Length { get; }

    /// <summary>Creates an upload from an in-memory buffer. It can be read any number of times.</summary>
    /// <param name="content">Bytes to store.</param>
    /// <param name="fileName">Client file name.</param>
    /// <param name="contentType">Declared media type.</param>
    /// <returns>The upload.</returns>
    public static FileUpload FromBytes(byte[] content, string fileName, string? contentType = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fileName);
        return new FileUpload(() => new MemoryStream(content, writable: false), content.LongLength, fileName, contentType);
    }

    /// <summary>
    /// Creates an upload from a stream. A seekable stream can be re-read from its starting position; a
    /// non-seekable stream can be opened once only.
    /// </summary>
    /// <param name="content">Source stream. The caller keeps ownership and disposes it.</param>
    /// <param name="fileName">Client file name.</param>
    /// <param name="contentType">Declared media type.</param>
    /// <param name="length">Length when known and the stream cannot report it.</param>
    /// <returns>The upload.</returns>
    public static FileUpload FromStream(Stream content, string fileName, string? contentType = null, long? length = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(fileName);
        if (content.CanSeek)
        {
            var start = content.Position;
            return new FileUpload(
                () =>
                {
                    content.Position = start;
                    return new NonDisposingStream(content);
                },
                content.Length - start,
                fileName,
                contentType);
        }

        FileUpload? self = null;
        self = new FileUpload(
            () =>
            {
                if (self!._consumed)
                {
                    throw new InvalidOperationException("This upload wraps a non-seekable stream and was already read.");
                }

                self._consumed = true;
                return new NonDisposingStream(content);
            },
            length,
            fileName,
            contentType);
        return self;
    }

    /// <summary>
    /// Creates an upload whose content is opened on demand, for example from <c>IFormFile.OpenReadStream</c>.
    /// Each opened stream is owned by, and disposed by, whoever opened it.
    /// </summary>
    /// <param name="open">Opens a new readable stream positioned at the start of the content.</param>
    /// <param name="fileName">Client file name.</param>
    /// <param name="contentType">Declared media type.</param>
    /// <param name="length">Content length when known.</param>
    /// <returns>The upload.</returns>
    public static FileUpload FromFactory(Func<Stream> open, string fileName, string? contentType = null, long? length = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(fileName);
        return new FileUpload(open, length, fileName, contentType);
    }

    /// <summary>
    /// Opens the content for reading. The returned stream can be disposed freely: for stream-backed uploads
    /// disposing it leaves the caller's source stream open.
    /// </summary>
    /// <returns>A readable stream positioned at the start of the content.</returns>
    /// <exception cref="InvalidOperationException">The upload wraps a non-seekable stream that was already read.</exception>
    public Stream OpenReadStream() => _open();
}
