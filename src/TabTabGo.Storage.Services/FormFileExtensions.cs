using Microsoft.AspNetCore.Http;
using TabTabGo.Storage.Models;

namespace TabTabGo.Storage.Services;

/// <summary>Adapts ASP.NET Core uploads to the framework-free <see cref="FileUpload"/>.</summary>
public static class FormFileExtensions
{
    /// <summary>Wraps an <see cref="IFormFile"/>. The content is opened lazily and disposed by the consumer.</summary>
    /// <param name="file">The uploaded file.</param>
    /// <returns>An upload carrying the file name, declared content type and length.</returns>
    public static FileUpload ToFileUpload(this IFormFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        return FileUpload.FromFactory(file.OpenReadStream, file.FileName, file.ContentType, file.Length);
    }
}
