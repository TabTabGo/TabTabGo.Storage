using System.Diagnostics.CodeAnalysis;

namespace TabTabGo.Storage.ContentTypes;

/// <summary>
/// Maps a file extension to a media type. The stored extension, not the client-declared media type, decides what
/// is served, which closes the stored-XSS route through client-chosen content types.
/// </summary>
public static class ContentTypeMap
{
    /// <summary>Media type used when the extension is unknown.</summary>
    public const string DefaultContentType = "application/octet-stream";

    private static readonly Dictionary<string, string> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["bmp"] = "image/bmp",
        ["ico"] = "image/x-icon",
        ["webp"] = "image/webp",
        ["tif"] = "image/tiff",
        ["tiff"] = "image/tiff",
        ["pdf"] = "application/pdf",
        ["txt"] = "text/plain",
        ["csv"] = "text/csv",
        ["json"] = "application/json",
        ["zip"] = "application/zip",
        ["doc"] = "application/msword",
        ["docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ["xls"] = "application/vnd.ms-excel",
        ["xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        ["ppt"] = "application/vnd.ms-powerpoint",
        ["pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
        ["mp3"] = "audio/mpeg",
        ["wav"] = "audio/wav",
        ["ogg"] = "audio/ogg",
        ["m4a"] = "audio/mp4",
        ["mp4"] = "video/mp4",
        ["webm"] = "video/webm",
        ["mov"] = "video/quicktime",
        ["avi"] = "video/x-msvideo",
    };

    /// <summary>
    /// Extensions a browser can execute or render with script (HTML, SVG, XML/XSLT, JavaScript). They are never mapped
    /// to their native media type: they are served as <see cref="DefaultContentType"/> and only as attachments.
    /// </summary>
    public static readonly IReadOnlySet<string> ActiveContentExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "html", "htm", "xhtml", "xht", "svg", "svgz", "xml", "xsl", "xslt", "js", "mjs", "hta", "shtml",
    };

    /// <summary>Returns whether the extension is script-capable content (see <see cref="ActiveContentExtensions"/>).</summary>
    /// <param name="extension">Extension with or without the dot.</param>
    /// <returns><c>true</c> for active content.</returns>
    public static bool IsActiveContent(string? extension) =>
        !string.IsNullOrWhiteSpace(extension) && ActiveContentExtensions.Contains(extension.TrimStart('.'));

    /// <summary>
    /// Returns whether a media type is safe to render inline: images (not SVG), PDF, plain text, audio and video.
    /// </summary>
    /// <param name="contentType">Media type to test.</param>
    /// <returns><c>true</c> when safe to render inline.</returns>
    public static bool IsSafeToRender(string? contentType)
    {
        if (string.IsNullOrWhiteSpace(contentType))
        {
            return false;
        }

        var type = contentType.Split(';')[0].Trim().ToLowerInvariant();
        if (type.Contains("svg", StringComparison.Ordinal) || type.Contains("xml", StringComparison.Ordinal))
        {
            return false;
        }

        return type.StartsWith("image/", StringComparison.Ordinal) || type == "application/pdf" || type == "text/plain" ||
               type.StartsWith("audio/", StringComparison.Ordinal) || type.StartsWith("video/", StringComparison.Ordinal);
    }

    /// <summary>
    /// Chooses the response content type for a download: the requested one only when it is safe to render, otherwise
    /// the type derived from the object's extension.
    /// </summary>
    /// <param name="requested">Caller-requested content type, if any.</param>
    /// <param name="objectPath">Object path whose extension is the fallback.</param>
    /// <returns>A safe content type.</returns>
    public static string ChooseResponseType(string? requested, string objectPath) =>
        !IsActiveContent(Path.GetExtension(objectPath)) && IsSafeToRender(requested)
            ? requested!
            : FromExtension(Path.GetExtension(objectPath));

    /// <summary>Looks up the media type for an extension.</summary>
    /// <param name="extension">Extension with or without the leading dot.</param>
    /// <param name="contentType">The media type when known.</param>
    /// <returns><c>true</c> when the extension is known.</returns>
    public static bool TryGet(string? extension, [NotNullWhen(true)] out string? contentType)
    {
        contentType = null;
        if (string.IsNullOrWhiteSpace(extension))
        {
            return false;
        }

        return Map.TryGetValue(extension.TrimStart('.'), out contentType);
    }

    /// <summary>Returns the media type for an extension, or <see cref="DefaultContentType"/>.</summary>
    /// <param name="extension">Extension with or without the leading dot.</param>
    /// <returns>A media type.</returns>
    public static string FromExtension(string? extension) =>
        TryGet(extension, out var contentType) ? contentType : DefaultContentType;
}
