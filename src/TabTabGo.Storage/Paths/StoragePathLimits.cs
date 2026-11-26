namespace TabTabGo.Storage.Paths;

/// <summary>
/// Length limits a provider applies to a path on top of the safety rules of <see cref="StoragePath"/>.
/// </summary>
/// <param name="MaxSegmentLength">Longest allowed single segment.</param>
/// <param name="MaxLength">Longest allowed whole normalised path (separators included).</param>
/// <param name="CountUtf8Bytes"><c>true</c> to measure UTF-8 bytes, <c>false</c> to measure UTF-16 characters.</param>
public sealed record StoragePathLimits(int MaxSegmentLength, int MaxLength, bool CountUtf8Bytes = true)
{
    /// <summary>File-system limit: a segment is at most 255 UTF-8 bytes (ext4, XFS, APFS, NTFS all accept that).</summary>
    public static StoragePathLimits FileSystem { get; } = new(255, int.MaxValue);

    /// <summary>Azure Blob limit: a blob name is at most 1024 characters.</summary>
    public static StoragePathLimits AzureBlobName { get; } = new(int.MaxValue, 1024, CountUtf8Bytes: false);

    /// <summary>S3 limit: an object key is at most 1024 UTF-8 bytes, the container prefix included.</summary>
    /// <param name="container">Container (key prefix) the path will be stored under.</param>
    /// <returns>Limits for the path relative to the container.</returns>
    public static StoragePathLimits S3ObjectKey(string? container) =>
        new(int.MaxValue, 1024 - System.Text.Encoding.UTF8.GetByteCount(container ?? string.Empty) - 1);
}
