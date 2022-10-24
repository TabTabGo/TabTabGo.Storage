using Microsoft.AspNetCore.Http;
using System.ComponentModel.DataAnnotations.Schema;
using TabTabGo.Core.Entities;
using TabTabGo.Storage.Enums;

namespace TabTabGo.Storage.Entities;

public class File : Entity, IFile
{
    public long FileId { get; set; }
    public string ReferenceType { get; set; }
    public long? ReferenceId { get; set; }
    public int? Duration { get; set; }
    public string Title { get; set; }
    public string FileExtension { get; set; } // FileType
    public FileType FileType { get; set; }

    public string OriginalFileName { get; set; } // OriginalFileName
    public string FilePath { get; set; } // FilePath
    public long? FileSize { get; set; } // FileSize
    public string Comment { get; set; } // Comment
    public string OriginalMediaType { get; set; } // OriginalMediaType
    public bool? IsCompressed { get; set; } // IsCompressed

    [NotMapped] public IFormFile FormFile { get; set; }
}