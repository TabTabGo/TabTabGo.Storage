using Microsoft.Extensions.Logging;
using TabTabGo.Core.Infrastructure.Data;
using TabTabGo.Storage.Entities;
using TabTabGo.Storage.Services;

namespace TabTabGo.Storage.WebApi.Controllers;

public class FileController : FileBaseController<File>
{
    public FileController(IFileService<File> service, ILogger<FileBaseController<File>> logger, IUnitOfWork unitOfWork) : base(service, logger, unitOfWork)
    {
    }
}