using TabTabGo.Core.Entities;
using TabTabGo.Storage.Enums;

namespace TabTabGo.Storage.Entities;

/// <summary>
/// Extension point for consumer file entities. Implement it on your own entity to use
/// <c>FileService&lt;TFile&gt;</c>.
/// </summary>
public interface IFile : IEntity
{
    /// <summary>Primary key.</summary>
    long FileId { get; set; }

    /// <summary>Display title.</summary>
    string Title { get; set; }

    /// <summary>Stored extension including the leading dot.</summary>
    string FileExtension { get; set; }

    /// <summary>Kind of content; also the storage container name.</summary>
    FileType FileType { get; set; }

    /// <summary>Sanitised name the client uploaded the file with.</summary>
    string OriginalFileName { get; set; }

    /// <summary>Relative, normalised, <c>/</c>-separated path of the object inside its container.</summary>
    string FilePath { get; set; }

    /// <summary>Size in bytes.</summary>
    long? FileSize { get; set; }

    /// <summary>Free-text comment.</summary>
    string Comment { get; set; }

    /// <summary>Media type the client declared (reference only).</summary>
    string OriginalMediaType { get; set; }

    /// <summary>Whether the stored content is compressed.</summary>
    bool? IsCompressed { get; set; }
}
