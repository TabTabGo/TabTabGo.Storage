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
        ["svg"] = "image/svg+xml",
        ["tif"] = "image/tiff",
        ["tiff"] = "image/tiff",
        ["pdf"] = "application/pdf",
        ["txt"] = "text/plain",
        ["csv"] = "text/csv",
        ["html"] = "text/html",
        ["htm"] = "text/html",
        ["js"] = "text/javascript",
        ["mjs"] = "text/javascript",
        ["json"] = "application/json",
        ["xml"] = "application/xml",
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
