using TabTabGo.Core.Entities;
using TabTabGo.Storage.Enums;

namespace TabTabGo.Storage.Entities;

/// <summary>
/// Metadata of a stored file. The bytes live in an <see cref="IStorageProvider"/>; this row only points at them.
/// </summary>
/// <remarks>
/// <see cref="FilePath"/>, <see cref="FileSize"/>, <see cref="FileExtension"/>, <see cref="OriginalFileName"/> and
/// <see cref="OriginalMediaType"/> are server-owned: they are written by the upload use case and never taken from a
/// client update. Property annotations (and the null defaults) are deliberately unchanged from 6.0.8.4 because EF
/// Core derives column nullability from them (the golden model test pins the schema).
/// </remarks>
public class File : Entity, IFile
{
    /// <summary>Primary key.</summary>
    public long FileId { get; set; }

    /// <summary>Kind of record this file is attached to (consumer-defined).</summary>
    public string ReferenceType { get; set; } = null!;

    /// <summary>Id of the record this file is attached to.</summary>
    public long? ReferenceId { get; set; }

    /// <summary>Duration in seconds for audio and video.</summary>
    public int? Duration { get; set; }

    /// <summary>Display title.</summary>
    public string Title { get; set; } = null!;

    /// <summary>Stored extension including the leading dot (at most 10 characters).</summary>
    public string FileExtension { get; set; } = null!;

    /// <summary>Kind of content; also the storage container name.</summary>
    public FileType FileType { get; set; }

    /// <summary>Sanitised name the client uploaded the file with.</summary>
    public string OriginalFileName { get; set; } = null!;

    /// <summary>Relative, normalised, <c>/</c>-separated path of the object inside its container.</summary>
    public string FilePath { get; set; } = null!;

    /// <summary>Size in bytes.</summary>
    public long? FileSize { get; set; }

    /// <summary>Free-text comment.</summary>
    public string Comment { get; set; } = null!;

    /// <summary>Media type the client declared. Recorded for reference only; it is never served.</summary>
    public string OriginalMediaType { get; set; } = null!;

    /// <summary>Whether the stored content is compressed.</summary>
    public bool? IsCompressed { get; set; }
}
