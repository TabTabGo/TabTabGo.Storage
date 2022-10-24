using Microsoft.AspNetCore.Http;
using TabTabGo.Core.Entities;
using TabTabGo.Storage.Enums;

namespace TabTabGo.Storage.Entities;

public interface IFile : IEntity
{
    long FileId { get; set; }
    string Title { get; set; }
    string FileExtension { get; set; } // FileType
    FileType FileType { get; set; }
    string OriginalFileName { get; set; } // OriginalFileName
    string FilePath { get; set; } // FilePath
    long? FileSize { get; set; } // FileSize
    string Comment { get; set; } // Comment
    string OriginalMediaType { get; set; } // OriginalMediaType
    bool? IsCompressed { get; set; } // IsCompressed
    IFormFile FormFile { get; set; }
}