using TabTabGo.Storage.Enums;

namespace TabTabGo.Storage.Models;

/// <summary>Filter and paging for <c>IFileService&lt;TFile&gt;.ListAsync</c>.</summary>
public sealed class FileListQuery
{
    /// <summary>Largest accepted page size.</summary>
    public const int MaxPageSize = 100;

    /// <summary>Only files attached to this kind of record.</summary>
    public string? ReferenceType { get; set; }

    /// <summary>Only files attached to this record id.</summary>
    public long? ReferenceId { get; set; }

    /// <summary>Only files of this kind.</summary>
    public FileType? FileType { get; set; }

    /// <summary>1-based page number.</summary>
    public int Page { get; set; } = 1;

    /// <summary>Rows per page, 1 to <see cref="MaxPageSize"/>.</summary>
    public int PageSize { get; set; } = 20;
}
