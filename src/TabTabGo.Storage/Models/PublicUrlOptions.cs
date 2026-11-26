namespace TabTabGo.Storage.Models;

/// <summary>Options for a time-limited public URL (presigned URL or SAS).</summary>
public sealed class PublicUrlOptions
{
    /// <summary>Shortest accepted lifetime.</summary>
    public static readonly TimeSpan MinExpiry = TimeSpan.FromMinutes(1);

    /// <summary>Longest accepted lifetime (the S3 presign limit).</summary>
    public static readonly TimeSpan MaxExpiry = TimeSpan.FromDays(7);

    /// <summary>Requested lifetime. <c>null</c> uses the provider default. Clamped to [1 minute, 7 days].</summary>
    public TimeSpan? ExpiresIn { get; set; }

    /// <summary>
    /// Ignored for the response type: the content type of a signed URL always comes from the object's own extension
    /// (<c>ContentTypeMap</c>), never from the caller. The property is kept for source compatibility.
    /// </summary>
    public string? ContentType { get; set; }

    /// <summary>File name offered to the browser; encoded per RFC 5987 in the response disposition.</summary>
    public string? DownloadFileName { get; set; }

    /// <summary>
    /// Ask for <c>inline</c> instead of <c>attachment</c>. Honoured only for types that are safe to render from a signed
    /// URL (<c>ContentTypeMap.IsSafeToRenderSigned</c>); everything else is an attachment.
    /// </summary>
    public bool Inline { get; set; }

    /// <summary>Returns the lifetime to use: the requested one, or <paramref name="defaultExpiry"/>, clamped.</summary>
    /// <param name="defaultExpiry">Provider default when <see cref="ExpiresIn"/> is not set.</param>
    /// <returns>A lifetime within [<see cref="MinExpiry"/>, <see cref="MaxExpiry"/>].</returns>
    public TimeSpan GetEffectiveExpiry(TimeSpan defaultExpiry)
    {
        var value = ExpiresIn ?? defaultExpiry;
        if (value < MinExpiry)
        {
            return MinExpiry;
        }

        return value > MaxExpiry ? MaxExpiry : value;
    }
}
